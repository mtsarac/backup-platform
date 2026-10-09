import { useEffect, useRef, useState } from 'react'
import { useMutation, useQuery } from '@tanstack/react-query'
import { Button } from '@/components/ui/button'
import { nativeDownloadUrl } from '@/features/artifacts/native-download-url'
import { artifactSize } from '@/features/artifacts/format'
import { failureMessage } from '@/lib/api/failure-messages'
import { useVisibleTab } from '@/hooks/use-visible-tab'
import { getSupportDownloadAvailability, getSupportDownloadStatus, prepareSupportDownload } from './api'
import { supportKeys } from './hooks'
import { SupportQueryState } from './support-query-state'
import type { SupportScope } from './types'

export function SupportArtifactDownload({ scope, artifactId, eligible }: { scope: SupportScope; artifactId: string; eligible: boolean }) {
  return <DownloadController key={`${scope.sessionId}:${scope.tenantId}:${scope.enabled}:${eligible}:${artifactId}`} scope={scope} artifactId={artifactId} eligible={eligible} />
}
function DownloadController({ scope, artifactId, eligible }: { scope: SupportScope; artifactId: string; eligible: boolean }) {
  const active = useRef(true)
  const started = useRef(false)
  const submitting = useRef(false)
  const [replicaId, setReplicaId] = useState('')
  const enabled = scope.enabled && eligible
  const visible = useVisibleTab()
  useEffect(() => { active.current = true; return () => { active.current = false } }, [])
  const availability = useQuery({ queryKey: supportKeys.data(scope, 'downloads', 'availability', artifactId), queryFn: ({ signal }) => getSupportDownloadAvailability(scope.sessionId, artifactId, signal), enabled, refetchInterval: visible ? 10_000 : false })
  const prepare = useMutation({ mutationKey: supportKeys.data(scope, 'downloads', 'prepare', artifactId), mutationFn: () => prepareSupportDownload(scope.sessionId, artifactId, replicaId || undefined), retry: false, onSettled: () => { submitting.current = false } })
  const download = prepare.data
  const safeUrl = download && download.downloadUrl.startsWith('/') && !download.downloadUrl.startsWith('//') && nativeDownloadUrl(download.downloadUrl) && !/[?#]/.test(download.downloadUrl)
  const status = useQuery({ queryKey: supportKeys.data(scope, 'downloads', 'status', artifactId, download?.sessionId), queryFn: ({ signal }) => getSupportDownloadStatus(scope.sessionId, artifactId, download!.sessionId, signal), enabled: enabled && Boolean(download), retry: false, refetchInterval: query => visible && !query.state.error && !['Completed', 'Failed', 'Cancelled'].includes(query.state.data?.state ?? '') ? 1000 : false })
  useEffect(() => {
    if (!active.current || !enabled || !download || !safeUrl || status.data?.state !== 'Ready' || started.current) return
    started.current = true
    const anchor = document.createElement('a')
    // Exact relative API URL, cookie-authenticated browser navigation. No file bytes or tokens in JS.
    anchor.setAttribute('href', download.downloadUrl)
    anchor.download = download.filename
    document.body.appendChild(anchor)
    anchor.click()
    anchor.remove()
  }, [download, enabled, safeUrl, status.data?.state])
  const selected = availability.data?.replicas.find(replica => replica.replicaId === replicaId)
  const busy = prepare.isPending || Boolean(download && !status.error && safeUrl && !['Completed', 'Failed', 'Cancelled'].includes(status.data?.state ?? ''))
  const error = availability.error || prepare.error || status.error
  return <section aria-label="Download Backup" className="space-y-3 rounded-lg border border-border bg-surface p-4">
    <h3 className="font-medium">Download Backup</h3>
    {!scope.enabled && <p className="text-sm text-text-muted">Etkin, sahibi olduğunuz ve DownloadBackup izni taşıyan bir destek oturumu gerekir.</p>}
    {!eligible && <p className="text-sm text-text-muted">Bu Artifact kullanılabilir değil; indirme kapalı.</p>}
    {enabled && <SupportQueryState isPending={availability.isPending} error={error}>{availability.data && <>
      <p className="text-sm">{artifactSize(availability.data.sizeBytes)} · Checksum: <code className="break-all">{availability.data.checksum}</code></p>
      <label className="block text-sm">Replica<select className="mt-1 block w-full rounded-lg border border-border bg-surface p-2" value={replicaId} disabled={busy} onChange={event => setReplicaId(event.target.value)}><option value="">Otomatik · kullanılabilir Replica</option>{availability.data.replicas.map(replica => <option key={replica.replicaId} value={replica.replicaId} disabled={!replica.available}>{replica.storageProvider}{!replica.available ? ` · ${failureMessage(replica.reason)}` : ''}</option>)}</select></label>
      {!availability.data.available && <p role="status">İndirme kullanılamıyor: {failureMessage(availability.data.reason)}</p>}
    </>}</SupportQueryState>}
    <Button disabled={!enabled || busy || Boolean(error) || !availability.data?.available || Boolean(selected && !selected.available)} onClick={() => { if (submitting.current || !enabled) return; submitting.current = true; started.current = false; prepare.mutate() }}>{busy ? 'İndirme hazırlanıyor…' : 'Download'}</Button>
    {download && !safeUrl && <p role="alert" className="text-sm text-error">İndirme bağlantısı güvenli değil. Yeni bir indirme başlatın.</p>}
    {status.data && <p role={status.data.state === 'Failed' ? 'alert' : 'status'} className="text-sm">{status.data.state === 'Failed' ? failureMessage(status.data.failureCode) : status.data.state === 'Preparing' ? 'İndirme hazırlanıyor…' : status.data.state === 'Ready' || status.data.state === 'Streaming' ? 'İndirme başlatıldı. Dosyayı tarayıcınız kaydediyor.' : status.data.state === 'Completed' ? 'İndirme tamamlandı.' : 'İndirme iptal edildi.'} · {artifactSize(status.data.transferredBytes)}</p>}
  </section>
}
