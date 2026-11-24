import { useQueryClient } from '@tanstack/react-query'
import { Button } from '@/components/ui/button'
import { ErrorNotice } from '@/components/ui/notice'
import { getDownloadStatus, prepareDownload } from '@/features/artifacts/api'
import { DownloadFlowStatus } from '@/features/artifacts/download-flow-status'
import { useDownloadFlow } from '@/features/artifacts/use-download-flow'
import { tx, useLanguage } from '@/lib/i18n'
import { downloadAttemptsKey } from './hooks'
import type { BackupShareView } from './types'

function refusal(err: unknown): string {
  const e = (err && typeof err === 'object' ? err : {}) as { code?: string; reason?: string; error?: string; status?: number; message?: string }
  const code = e.code || e.error || e.reason || e.message || ''
  if (code === 'replica_unavailable' || code === 'no_downloadable_replica') return tx('Kopya şu anda indirme için kullanılamıyor.')
  if (code === 'share_revoked') return tx('Paylaşım iptal edildiği için indirme yapılamaz.')
  if (code === 'share_expired' || e.status === 403 || code.includes('403')) return tx('İndirme yetkisi reddedildi veya paylaşım süresi doldu.')
  return tx('İndirme başlatılamadı.')
}

/** The same download flow as the artifact page: agent-direct copies go over WebRTC, backend copies are saved once the session is Ready. */
export function ShareDownload({ share }: { share: BackupShareView }) {
  useLanguage()
  const queryClient = useQueryClient()
  const flow = useDownloadFlow({
    prepare: async () => {
      try { return await prepareDownload(share.tenantId, share.artifactId, undefined, undefined, share.id) }
      finally { void queryClient.invalidateQueries({ queryKey: downloadAttemptsKey }) }
    },
    fetchStatus: (sessionId, signal) => getDownloadStatus(share.tenantId, share.artifactId, sessionId, signal),
    statusKey: sessionId => ['shares', share.id, 'download-status', sessionId],
  })
  return <div className="flex flex-col items-end gap-2">
    <Button size="sm" variant="outline" disabled={flow.busy} onClick={() => void flow.start()}>
      {flow.pending || flow.state === 'Preparing' ? tx('İndirme hazırlanıyor…') : tx('İndir')}
    </Button>
    {flow.error ? <ErrorNotice what={refusal(flow.error)} /> : null}
    <DownloadFlowStatus flow={flow} hideError />
  </div>
}
