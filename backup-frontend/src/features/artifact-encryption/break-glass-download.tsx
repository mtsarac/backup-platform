import { tx, useLanguage } from '@/lib/i18n'
import { useQueryClient } from '@tanstack/react-query'
import { Button } from '@/components/ui/button'
import { Notice } from '@/components/ui/notice'
import { DownloadFlowStatus } from '@/features/artifacts/download-flow-status'
import { useDownloadFlow } from '@/features/artifacts/use-download-flow'
import { downloadBreakGlass, getBreakGlassDownloadStatus } from './api'
import { encryptionKeys } from './hooks'


/** The approved grant is spent once; direct external downloads verify and publish through an explicit disk-save action. */
/** Stays mounted through the Approved -> Consumed flip so the progress of the transfer it started remains visible. */
export function BreakGlassDownload({ requestId, canStart }: { requestId: string; canStart: boolean }) {
  useLanguage()
  const client = useQueryClient()
  const flow = useDownloadFlow({
    prepare: () => downloadBreakGlass(requestId),
    fetchStatus: (sessionId, signal) => getBreakGlassDownloadStatus(requestId, sessionId, signal),
    statusKey: sessionId => [...encryptionKeys.breakGlass(requestId), 'download', sessionId],
    onPrepared: () => { void client.invalidateQueries({ queryKey: encryptionKeys.breakGlass(requestId), exact: true }) },
  })
  if (!flow.session && !canStart && !flow.pending) return null
  return <div className="space-y-3">
    {!flow.session && <Notice tone="warning" title={tx("Tek kullanımlık çözülmüş indirme")}>{tx("İndirme başladığı anda bu hak kullanılmış sayılır; aktarım başarısız olsa bile aynı istek yeniden kullanılamaz. Dosya önce güvenli geçici alanda çözülür ve doğrulanır; doğrulama bitmeden hiçbir veri gönderilmez. İndirilen dosya şifresizdir, güvenli bir yerde saklayın.")}</Notice>}
    {!flow.session && <Button disabled={flow.busy} onClick={() => void flow.start()}>{flow.pending ? tx('İndirme hazırlanıyor…') : tx('Çözülmüş dosyayı indir')}</Button>}
    <DownloadFlowStatus flow={flow} unsafeAction={tx("Yeni bir istek açın.")} />
  </div>
}
