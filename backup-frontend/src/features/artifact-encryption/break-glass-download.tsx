import { tx, useLanguage } from '@/lib/i18n'
import { useEffect, useRef } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Button } from '@/components/ui/button'
import { Notice } from '@/components/ui/notice'
import { artifactSize } from '@/features/artifacts/format'
import { nativeDownloadUrl } from '@/features/artifacts/native-download-url'
import { useVisibleTab } from '@/hooks/use-visible-tab'
import { errorMessage, failureMessage } from '@/lib/api/failure-messages'
import { downloadBreakGlass, getBreakGlassDownloadStatus } from './api'
import { encryptionKeys } from './hooks'
import { DirectDownloadButton } from '@/features/artifacts/direct-download-button'

const finished = ['Completed', 'Failed', 'Cancelled']

/** The approved grant is spent once; direct external downloads verify and publish through an explicit disk-save action. */
/** Stays mounted through the Approved -> Consumed flip so the progress of the transfer it started remains visible. */
export function BreakGlassDownload({ requestId, canStart }: { requestId: string; canStart: boolean }) {
  useLanguage()
  const client = useQueryClient()
  const visible = useVisibleTab()
  const started = useRef(false)
  const submitting = useRef(false)
  const start = useMutation({ mutationFn: () => downloadBreakGlass(requestId), retry: false, onSuccess: () => { void client.invalidateQueries({ queryKey: encryptionKeys.breakGlass(requestId), exact: true }) }, onSettled: () => { submitting.current = false } })
  const session = start.data
  const url = session && session.downloadUrl.startsWith('/') && !session.downloadUrl.startsWith('//') && !/[?#]/.test(session.downloadUrl) ? nativeDownloadUrl(session.downloadUrl) : undefined
  const status = useQuery({
    queryKey: [...encryptionKeys.breakGlass(requestId), 'download', session?.sessionId],
    queryFn: ({ signal }) => getBreakGlassDownloadStatus(requestId, session!.sessionId, signal),
    enabled: Boolean(session), retry: false,
    refetchInterval: query => visible && !query.state.error && !finished.includes(query.state.data?.state ?? '') ? 1000 : false,
  })
  useEffect(() => {
    if (!session || session.direct || !url || status.data?.state !== 'Ready' || started.current) return
    started.current = true
    const anchor = document.createElement('a')
    anchor.href = url
    anchor.download = session.filename
    document.body.appendChild(anchor)
    anchor.click()
    anchor.remove()
  }, [session, url, status.data?.state])
  const state = status.data?.state
  const busy = start.isPending || Boolean(session && !status.error && url && !finished.includes(state ?? ''))
  const error = start.error ?? status.error
  if (!session && !canStart && !start.isPending) return null
  return <div className="space-y-3">
    {!session && <Notice tone="warning" title={tx("Tek kullanımlık çözülmüş indirme")}>{tx("İndirme başladığında istek tüketilir; aktarım başarısız olsa bile aynı istek tekrar kullanılamaz. Dosyanın tamamı güvenli geçici alanda çözülür; şifreli ve düz veri checksum doğrulanmadan hiçbir düz veri gönderilmez. İndirilen dosya şifresizdir.")}</Notice>}
    {error && <Notice tone="error">{errorMessage(error)}</Notice>}
    {session && !url && <Notice tone="error">{tx("İndirme bağlantısı güvenli değil. Yeni bir istek açın.")}</Notice>}
    {!session && <Button disabled={busy} onClick={() => { if (submitting.current) return; submitting.current = true; started.current = false; start.mutate() }}>{start.isPending ? tx('İndirme hazırlanıyor…') : tx('Çözülmüş dosyayı indir')}</Button>}
    {session?.direct && <DirectDownloadButton key={session.sessionId} session={session} />}
    {session && url && !state && !status.error && <p role="status" className="text-sm text-text-muted">{tx("İndirme hazırlanıyor…")}</p>}
    {state && <Notice tone={state === 'Failed' ? 'error' : state === 'Completed' ? 'success' : 'info'}>
      {state === 'Preparing' ? tx('İndirme hazırlanıyor…') : state === 'Ready' || state === 'Streaming' ? tx('İndirme başlatıldı. Dosya tarayıcınız tarafından kaydediliyor.') : state === 'Completed' ? tx('Dosya aktarımı tamamlandı.') : state === 'Cancelled' ? tx('İndirme iptal edildi.') : tx("İndirme başarısız: {0}", { "0": failureMessage(status.data?.failureCode) })}
      {(status.data?.transferredBytes ?? 0) > 0 && tx(" · {0} aktarıldı", { "0": artifactSize(status.data?.transferredBytes ?? 0) })}
    </Notice>}
  </div>
}
