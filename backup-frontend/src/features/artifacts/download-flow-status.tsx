import { tx, useLanguage } from '@/lib/i18n'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { formatBytes } from '@/lib/format'
import { DirectDownloadButton } from './direct-download-button'
import type { DownloadFlow } from './use-download-flow'

/** What a started download shows: unsafe link, the direct-save button, progress, completion or the failure with its next step. */
export function DownloadFlowStatus({ flow, unsafeAction = tx('Yeni bir indirme başlatın.'), hideError = false }: { flow: DownloadFlow; unsafeAction?: string; /** The caller already shows `flow.error` (for example inside its own access-denied handling). */ hideError?: boolean }) {
  useLanguage()
  const { session, status, state, unsafe, error } = flow
  return <>
    {unsafe && <ErrorNotice what={tx("İndirme bağlantısı güvenli değil.")} dataImpact={tx("Dosya indirilmedi; yedeğiniz değişmedi.")} action={unsafeAction} />}
    {error && !hideError ? <ErrorNotice error={error} /> : null}
    {session?.direct && <DirectDownloadButton key={session.sessionId} session={session} onFinished={flow.forget} />}
    {session && !unsafe && !status && !error && <p role="status" className="text-sm text-text-muted">{tx("İndirme hazırlanıyor…")}</p>}
    {status && state === 'Failed' && <ErrorNotice title={tx("İndirme başarısız")} failureCode={status.failureCode} />}
    {status && state !== 'Failed' && <Notice tone={state === 'Completed' ? 'success' : 'info'}>
      {state === 'Preparing' ? tx('İndirme hazırlanıyor…') : state === 'Ready' || state === 'Streaming' ? tx('İndirme başlatıldı. Dosya tarayıcınız tarafından kaydediliyor.') : state === 'Completed' ? tx('Dosya aktarımı tamamlandı.') : tx('İndirme iptal edildi.')}
      {status.transferredBytes > 0 && tx(" · {0} aktarıldı", { "0": formatBytes(status.transferredBytes) })}
    </Notice>}
  </>
}
