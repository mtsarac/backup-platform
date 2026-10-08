import { useEffect, useRef, useState } from 'react'
import { useMutation, useQuery } from '@tanstack/react-query'
import { Button } from '@/components/ui/button'
import { API_BASE_URL } from '@/lib/api/client'
import { errorMessage, failureMessage } from '@/lib/api/failure-messages'
import { getDownloadAvailability, getDownloadStatus, prepareDownload } from './api'
import { artifactSize } from './format'

function nativeDownloadUrl(path: string | undefined): string | undefined {
  if (!path) return undefined
  try {
    const base = new URL(`${API_BASE_URL}/`, window.location.origin)
    const url = new URL(path, base)
    return url.origin === base.origin && url.pathname.startsWith(base.pathname) ? url.href : undefined
  } catch {
    return undefined
  }
}
export function ArtifactDownload({ tenantId, artifactId, canAdmin, eligible }: { tenantId: string; artifactId: string; canAdmin: boolean; eligible: boolean }) {
  // Remount the controller on scope/permission changes: pending requests cannot initiate a download in a new scope.
  return <DownloadController key={`${tenantId}:${artifactId}:${canAdmin}:${eligible}`} tenantId={tenantId} artifactId={artifactId} canAdmin={canAdmin} eligible={eligible} />
}
function DownloadController({ tenantId, artifactId, canAdmin, eligible }: { tenantId: string; artifactId: string; canAdmin: boolean; eligible: boolean }) {
  const active = useRef(true)
  const started = useRef(false)
  const [replicaId, setReplicaId] = useState('')
  const [copyNotice, setCopyNotice] = useState('')
  useEffect(() => { active.current = true; return () => { active.current = false } }, [])
  const availability = useQuery({
    queryKey: ['tenant', tenantId, 'backup-artifacts', 'download-availability', artifactId, canAdmin],
    queryFn: ({ signal }) => getDownloadAvailability(tenantId, artifactId, signal), enabled: canAdmin && eligible,
    refetchInterval: 10_000,
  })
  const prepare = useMutation({ mutationFn: () => prepareDownload(tenantId, artifactId, replicaId || undefined) })
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
  if (!canAdmin) return <p className="text-sm text-text-muted">Download Backup yalnızca Owner / Admin için açıktır.</p>
  if (!eligible) return <p role="status" className="text-sm text-text-muted">Bu Backup silme sürecinde veya silinmiş; indirme kullanılamıyor.</p>
  const selected = availability.data?.replicas.find(replica => replica.replicaId === replicaId)
  const busy = prepare.isPending || Boolean(session && !status.error && !navigationError && (!status.data || ['Preparing', 'Ready', 'Streaming'].includes(status.data.state)))
  const failure = prepare.error || status.error || availability.error
  async function copyChecksum() {
    try { await navigator.clipboard.writeText(availability.data!.checksum); setCopyNotice('Checksum kopyalandı.') }
    catch { setCopyNotice('Checksum kopyalanamadı. Değeri seçerek kopyalayabilirsiniz.') }
  }
  return <section aria-label="Download Backup" className="space-y-3 rounded-lg border border-border bg-surface p-5">
    <h2 className="font-semibold text-text">Download Backup</h2>
    {availability.isPending && <p role="status">İndirme kullanılabilirliği kontrol ediliyor…</p>}
    {failure && <p role="alert" className="text-sm text-destructive">{errorMessage(failure)}</p>}
    {navigationError && <p role="alert" className="text-sm text-destructive">İndirme bağlantısı güvenli değil. Yeni bir indirme başlatın.</p>}
    {availability.data && <>
      <p className="text-sm text-text-muted">{artifactSize(availability.data.sizeBytes)} · SHA256</p>
      <code className="block break-all text-sm">{availability.data.checksum}</code>
      <Button variant="outline" onClick={copyChecksum}>Checksum kopyala</Button>
      {copyNotice && <p role="status" className="text-sm">{copyNotice}</p>}
      <label className="block text-sm">İndirme hedefi
        <select aria-label="İndirme hedefi" className="mt-1 block w-full rounded-md border border-border bg-surface p-2" value={replicaId} disabled={busy} onChange={event => setReplicaId(event.target.value)}>
          <option value="">Otomatik · kullanılabilir Replica</option>
          {availability.data.replicas.map(replica => <option key={replica.replicaId} value={replica.replicaId} disabled={!replica.available}>{replica.storageProvider} · {replica.storageTarget}{!replica.available ? ` · ${failureMessage(replica.reason)}` : ''}</option>)}
        </select>
      </label>
      {!availability.data.available && <p role="status">İndirme kullanılamıyor: {failureMessage(availability.data.reason)}</p>}
      <Button disabled={busy || !availability.data.available || Boolean(selected && !selected.available)} onClick={() => { started.current = false; prepare.mutate() }}>{prepare.isPending || status.data?.state === 'Preparing' ? 'İndirme hazırlanıyor…' : 'Download Backup'}</Button>
    </>}
    {session && !status.data && !status.error && <p role="status">İndirme hazırlanıyor…</p>}
    {status.data && <p role={status.data.state === 'Failed' ? 'alert' : 'status'} className="text-sm">
      {status.data.state === 'Preparing' ? 'İndirme hazırlanıyor…' : status.data.state === 'Ready' || status.data.state === 'Streaming' ? 'İndirme başlatıldı. Dosya tarayıcınız tarafından kaydediliyor.' : status.data.state === 'Completed' ? 'Dosya aktarımı tamamlandı.' : status.data.state === 'Cancelled' ? 'İndirme iptal edildi.' : `İndirme başarısız: ${failureMessage(status.data.failureCode)}`}
      {status.data.transferredBytes > 0 && ` · ${artifactSize(status.data.transferredBytes)} aktarıldı`}
    </p>}
  </section>
}
