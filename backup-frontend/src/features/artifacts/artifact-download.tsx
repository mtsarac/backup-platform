import { tx, useLanguage } from '@/lib/i18n'
import { Notice } from '@/components/ui/notice'
import { Fact, Facts, Section } from '@/components/ui/section'
import { selectClass } from '@/features/platform/styles'
import { useEffect, useRef, useState } from 'react'
import { useMutation, useQuery } from '@tanstack/react-query'
import { Button } from '@/components/ui/button'
import { nativeDownloadUrl } from './native-download-url'
import { errorMessage, failureMessage } from '@/lib/api/failure-messages'
import { getDownloadAvailability, getDownloadStatus, prepareDownload } from './api'
import { artifactSize } from './format'

export function ArtifactDownload({ tenantId, artifactId, canAdmin, eligible }: { tenantId: string; artifactId: string; canAdmin: boolean; eligible: boolean }) {
  useLanguage()
  // Remount the controller on scope/permission changes: pending requests cannot initiate a download in a new scope.
  return <DownloadController key={`${tenantId}:${artifactId}:${canAdmin}:${eligible}`} tenantId={tenantId} artifactId={artifactId} canAdmin={canAdmin} eligible={eligible} />
}
function DownloadController({ tenantId, artifactId, canAdmin, eligible }: { tenantId: string; artifactId: string; canAdmin: boolean; eligible: boolean }) {
  useLanguage()
  const active = useRef(true)
  const started = useRef(false)
  const [replicaId, setReplicaId] = useState('')
  const [decrypt, setDecrypt] = useState(false)
  const [copyNotice, setCopyNotice] = useState('')
  useEffect(() => { active.current = true; return () => { active.current = false } }, [])
  const availability = useQuery({
    queryKey: ['tenant', tenantId, 'backup-artifacts', 'download-availability', artifactId, canAdmin],
    queryFn: ({ signal }) => getDownloadAvailability(tenantId, artifactId, signal), enabled: canAdmin && eligible,
    refetchInterval: 10_000,
  })
  // The decrypt flag is only sent when chosen: the default request stays exactly the stored-file download.
  const prepare = useMutation({ mutationFn: () => decrypt ? prepareDownload(tenantId, artifactId, replicaId || undefined, true) : prepareDownload(tenantId, artifactId, replicaId || undefined) })
  const session = prepare.data
  const downloadUrl = nativeDownloadUrl(session?.downloadUrl)
  const navigationError = Boolean(session && !downloadUrl)
  const status = useQuery({
    queryKey: ['tenant', tenantId, 'backup-artifacts', 'download-status', artifactId, session?.sessionId],
    queryFn: ({ signal }) => getDownloadStatus(tenantId, artifactId, session!.sessionId, signal),
    enabled: canAdmin && eligible && Boolean(session),
    refetchInterval: (query) => query.state.data && ['Completed', 'Failed', 'Cancelled'].includes(query.state.data.state) ? false : 1000,
    retry: false,
  })
  useEffect(() => {
    if (!active.current || !canAdmin || !eligible || !session || !downloadUrl || status.data?.state !== 'Ready' || started.current) return
    // Use the browser's cookie-authenticated navigation; file bytes never enter JavaScript memory.
    started.current = true
    const anchor = document.createElement('a')
    anchor.href = downloadUrl
    anchor.download = session.filename
    document.body.appendChild(anchor)
    anchor.click()
    anchor.remove()
  }, [canAdmin, eligible, session, status.data?.state, downloadUrl])
  if (!canAdmin) return <Notice tone="info">{tx("Download Backup yalnızca Owner / Admin için açıktır.")}</Notice>
  if (!eligible) return <Notice tone="info">{tx("Bu Backup silme sürecinde veya silinmiş; indirme kullanılamıyor.")}</Notice>
  const selected = availability.data?.replicas.find(replica => replica.replicaId === replicaId)
  const busy = prepare.isPending || Boolean(session && !status.error && !navigationError && (!status.data || ['Preparing', 'Ready', 'Streaming'].includes(status.data.state)))
  const failure = prepare.error || status.error || availability.error
  async function copyChecksum() {
    try { await navigator.clipboard.writeText(availability.data!.checksum); setCopyNotice(tx('Checksum kopyalandı.')) }
    catch { setCopyNotice(tx('Checksum kopyalanamadı. Değeri seçerek kopyalayabilirsiniz.')) }
  }
  const finalState = status.data?.state
  return <Section title={tx("Download Backup")} description={tx("Dosya tarayıcınızdan doğrudan indirilir; içeriği uygulama belleğine alınmaz.")} className="max-w-3xl">
    <div className="space-y-4">
      {availability.isPending && <p role="status" className="text-sm text-text-muted">{tx("İndirme kullanılabilirliği kontrol ediliyor…")}</p>}
      {failure && <Notice tone="error">{errorMessage(failure)}</Notice>}
      {navigationError && <Notice tone="error">{tx("İndirme bağlantısı güvenli değil. Yeni bir indirme başlatın.")}</Notice>}
      {availability.data && <>
        <Facts columns={1}>
          <Fact label={tx("Boyut")}>{artifactSize(availability.data.sizeBytes)}</Fact>
          <Fact label={availability.data.format === 'v2-bpe' ? tx("Şifreli dosyanın SHA256 checksum değeri") : tx("SHA256 checksum")}>
            <code className="block break-all font-mono text-sm">{availability.data.checksum}</code>
            <Button className="mt-2" variant="outline" size="sm" onClick={copyChecksum}>{tx("Checksum kopyala")}</Button>
            {copyNotice && <p role="status" className="mt-1 text-sm text-text-muted">{copyNotice}</p>}
          </Fact>
          {session?.decrypted && <Fact label={tx("Çözülmüş dosyanın SHA256 checksum değeri")}><code className="block break-all font-mono text-sm">{session.checksum}</code></Fact>}
        </Facts>
        <label className="block text-sm font-medium text-text-heading">{tx("İndirme hedefi")}<select aria-label={tx("İndirme hedefi")} className={`${selectClass} w-full`} value={replicaId} disabled={busy} onChange={event => setReplicaId(event.target.value)}>
            <option value="">{tx("Otomatik · en uygun replika (Fallback destekli)")}</option>
            {availability.data.replicas.map(replica => <option key={replica.replicaId} value={replica.replicaId} disabled={!replica.available}>{replica.storageProvider} · {replica.storageTarget}{!replica.available ? ` · ${failureMessage(replica.reason)}` : ''}</option>)}
          </select>
        </label>
        {!replicaId && availability.data.replicas.filter(r => r.available).length > 1 && (
          <p className="text-sm text-text-muted">{tx("Otomatik seçimde, aktarım başlamadan önce backend'in okuyabildiği alternatif doğrulanmış replika denenebilir. Aktarım başladıktan sonra hata oluşursa indirme durur; otomatik yeniden başlatılmaz.")}</p>
        )}
        {availability.data.format === 'v2-bpe' && <fieldset className="space-y-2" disabled={busy}>
          <legend className="text-sm font-medium text-text-heading">{tx("İndirilecek dosya")}</legend>
          <label className="flex min-h-11 items-start gap-3 rounded-lg bg-surface-2 p-3 text-sm"><input type="radio" name="download-form" className="mt-1 size-4 accent-primary" checked={!decrypt} onChange={() => setDecrypt(false)} /><span><span className="block font-medium">{tx("Şifreli dosya (.bpe) · varsayılan")}</span><span className="block text-text-muted">{tx("Depolanan dosya olduğu gibi iner ve anahtar olmadan okunamaz. Çevrimdışı çözmek için bp-decrypt kullanılır.")}</span></span></label>
          <label className="flex min-h-11 items-start gap-3 rounded-lg bg-surface-2 p-3 text-sm"><input type="radio" name="download-form" className="mt-1 size-4 accent-primary" checked={decrypt} onChange={() => setDecrypt(true)} /><span><span className="block font-medium">{tx("Çözülmüş dosya")}</span><span className="block text-text-muted">{tx("Backend dosyanın tamamını güvenli geçici alanda çözer; şifreli ve düz veri checksum doğrulanmadan hiçbir düz veri gönderilmez. İşlem audit kaydına yazılır. İndirilen dosya şifresizdir, güvenle saklayın.")}</span></span></label>
        </fieldset>}
        {!availability.data.available && <Notice tone="warning">{tx("İndirme kullanılamıyor: Hiçbir doğrulanmış kopya erişilebilir değil (")}{failureMessage(availability.data.reason)}).</Notice>}
        <Button disabled={busy || !availability.data.available || Boolean(selected && !selected.available)} onClick={() => { started.current = false; prepare.mutate() }}>{prepare.isPending || finalState === 'Preparing' ? tx('İndirme hazırlanıyor…') : availability.data.format === 'v2-bpe' && decrypt ? tx('Çözülmüş dosyayı indir') : tx('Download Backup')}</Button>
      </>}
      {session && !status.data && !status.error && <p role="status" className="text-sm text-text-muted">{tx("İndirme hazırlanıyor…")}</p>}
      {status.data && <Notice tone={finalState === 'Failed' ? 'error' : finalState === 'Completed' ? 'success' : 'info'}>
        {finalState === 'Preparing' ? tx('İndirme hazırlanıyor…') : finalState === 'Ready' || finalState === 'Streaming' ? tx('İndirme başlatıldı. Dosya tarayıcınız tarafından kaydediliyor.') : finalState === 'Completed' ? tx('Dosya aktarımı tamamlandı.') : finalState === 'Cancelled' ? tx('İndirme iptal edildi.') : tx("İndirme başarısız: {0}", { "0": failureMessage(status.data.failureCode) })}
        {status.data.transferredBytes > 0 && tx(" · {0} aktarıldı", { "0": artifactSize(status.data.transferredBytes) })}
      </Notice>}
    </div>
  </Section>
}
