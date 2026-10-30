import { tx, useLanguage } from '@/lib/i18n'
import { RadioField } from '@/components/ui/radio-field'
import { Select } from '@/components/ui/text-input'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { Fact, Facts, Section } from '@/components/ui/section'
import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { failureMessage } from '@/lib/api/failure-messages'
import { getDownloadAvailability, getDownloadStatus, prepareDownload } from './api'
import { DownloadFlowStatus } from './download-flow-status'
import { useArtifactDownload } from './use-artifact-download'
import { formatBytes } from '@/lib/format'
import { storageProviderLabel } from '@/features/storage-connections/labels'

export function ArtifactDownload({ tenantId, artifactId, canAdmin, eligible }: { tenantId: string; artifactId: string; canAdmin: boolean; eligible: boolean }) {
  useLanguage()
  // Remount the controller on scope/permission changes: pending requests cannot initiate a download in a new scope.
  return <DownloadController key={`${tenantId}:${artifactId}:${canAdmin}:${eligible}`} tenantId={tenantId} artifactId={artifactId} canAdmin={canAdmin} eligible={eligible} />
}
function DownloadController({ tenantId, artifactId, canAdmin, eligible }: { tenantId: string; artifactId: string; canAdmin: boolean; eligible: boolean }) {
  useLanguage()
  const [decrypt, setDecrypt] = useState(false)
  const [copyNotice, setCopyNotice] = useState('')
  // The decrypt flag is only sent when chosen: the default request stays exactly the stored-file download.
  // Remounting on scope/permission changes (the key above) plus `enabled` keeps a pending request from starting a download in a new scope.
  const { availability, flow, replicaId, setReplicaId, selected } = useArtifactDownload({
    enabled: canAdmin && eligible,
    availabilityKey: ['tenant', tenantId, 'backup-artifacts', 'download-availability', artifactId, canAdmin],
    fetchAvailability: signal => getDownloadAvailability(tenantId, artifactId, signal),
    refetchInterval: 10_000,
    prepare: replica => decrypt ? prepareDownload(tenantId, artifactId, replica, true) : prepareDownload(tenantId, artifactId, replica),
    fetchStatus: (sessionId, signal) => getDownloadStatus(tenantId, artifactId, sessionId, signal),
    statusKey: sessionId => ['tenant', tenantId, 'backup-artifacts', 'download-status', artifactId, sessionId],
  })
  const { session } = flow
  if (!canAdmin) return <Notice tone="info">{tx("Download Backup yalnızca Owner / Admin için açıktır.")}</Notice>
  if (!eligible) return <Notice tone="info">{tx("Bu Backup silme sürecinde veya silinmiş; indirme kullanılamıyor.")}</Notice>
  const busy = flow.busy
  async function copyChecksum() {
    try { await navigator.clipboard.writeText(availability.data!.checksum); setCopyNotice(tx('Checksum kopyalandı.')) }
    catch { setCopyNotice(tx('Checksum kopyalanamadı. Değeri seçerek kopyalayabilirsiniz.')) }
  }
  return <Section title={tx("Download Backup")} description={tx("Harici depolama indirmeleri yetkili Agent üzerinden doğrudan diske aktarılır; platform depolaması tarayıcının indirme yöneticisini kullanır.")} className="max-w-3xl">
    <div className="space-y-4">
      {availability.isPending && <p role="status" className="text-sm text-text-muted">{tx("İndirme kullanılabilirliği kontrol ediliyor…")}</p>}
      {availability.error ? <ErrorNotice error={availability.error} /> : null}
      {availability.data && <>
        <Facts columns={1}>
          <Fact label={tx("Boyut")}>{formatBytes(availability.data.sizeBytes)}</Fact>
          <Fact label={availability.data.format === 'v2-bpe' ? tx("Şifreli dosyanın SHA256 checksum değeri") : tx("SHA256 checksum")}>
            <code className="block break-all font-mono text-sm">{availability.data.checksum}</code>
            <Button className="mt-2" variant="outline" size="sm" onClick={copyChecksum}>{tx("Checksum kopyala")}</Button>
            {copyNotice && <p role="status" className="mt-1 text-sm text-text-muted">{copyNotice}</p>}
          </Fact>
          {session?.decrypted && <Fact label={tx("Çözülmüş dosyanın SHA256 checksum değeri")}><code className="block break-all font-mono text-sm">{session.checksum}</code></Fact>}
        </Facts>
        <label className="block text-sm font-medium text-text-heading">{tx("İndirme hedefi")}<Select className="mt-1" aria-label={tx("İndirme hedefi")} value={replicaId} disabled={busy} onChange={event => setReplicaId(event.target.value)}>
            <option value="">{tx("Otomatik · en uygun doğrulanmış replika")}</option>
            {availability.data.replicas.map(replica => <option key={replica.replicaId} value={replica.replicaId} disabled={!replica.available}>{storageProviderLabel(replica.storageProvider)} · {replica.storageTarget}{!replica.available ? ` · ${failureMessage(replica.reason)}` : ''}</option>)}
          </Select>
        </label>
        {!replicaId && availability.data.replicas.filter(r => r.available).length > 1 && (
          <p className="text-sm text-text-muted">{tx("Harici depolamada backend veya TURN aktarımı kullanılmaz. Doğrudan yol kurulamazsa açık hata verilir.")}</p>
        )}
        {availability.data.format === 'v2-bpe' && <fieldset className="space-y-2" disabled={busy}>
          <legend className="text-sm font-medium text-text-heading">{tx("İndirilecek dosya")}</legend>
          <RadioField label={<span><span className="block font-medium">{tx("Şifreli dosya (.bpe) · varsayılan")}</span><span className="block text-text-muted">{tx("Depolanan dosya olduğu gibi iner ve anahtar olmadan okunamaz. Çevrimdışı çözmek için bp-decrypt kullanılır.")}</span></span>} className="items-start rounded-lg bg-surface-2 p-3" name="download-form" checked={!decrypt} onChange={() => setDecrypt(false)} />
          <RadioField label={<span><span className="block font-medium">{tx("Çözülmüş dosya")}</span><span className="block text-text-muted">{tx("Harici depolamada Agent dosyanın tamamını özel geçici alanda çözer; şifreli ve düz veri checksum doğrulanmadan düz veri gönderilmez. Yetkili anahtar erişimi audit kaydına yazılır. İndirilen dosya şifresizdir, güvenle saklayın.")}</span></span>} className="items-start rounded-lg bg-surface-2 p-3" name="download-form" checked={decrypt} onChange={() => setDecrypt(true)} />
        </fieldset>}
        {!availability.data.available && <Notice tone="warning">{tx("İndirme kullanılamıyor: Hiçbir doğrulanmış kopya erişilebilir değil (")}{failureMessage(availability.data.reason)}).</Notice>}
        <Button disabled={busy || !availability.data.available || Boolean(selected && !selected.available)} onClick={() => void flow.start()}>{flow.pending || flow.state === 'Preparing' ? tx('İndirme hazırlanıyor…') : availability.data.format === 'v2-bpe' && decrypt ? tx('Çözülmüş dosyayı indir') : tx('Download Backup')}</Button>
      </>}
      <DownloadFlowStatus flow={flow} />
    </div>
  </Section>
}
