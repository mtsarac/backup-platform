import { ExternalLink, RefreshCw } from 'lucide-react'
import { Link, useNavigate } from 'react-router-dom'
import { Button } from '@/components/ui/button'
import { buttonVariants } from '@/components/ui/button-variants'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { tx, useLanguage } from '@/lib/i18n'
import { GoogleOAuthNotice } from './google-oauth-flow'
import { testMessage, testTechnical } from './labels'
import type { ConnectionOps } from './hooks'
import type { ConnectionTestResponse, ConnectionView } from './types'
import type { GoogleOAuthOutcome } from './use-google-oauth'
import type { WizardReturn } from './wizard-providers'

interface ResultProps {
  created: ConnectionView
  google: boolean
  ops: ConnectionOps
  googleOutcome: GoogleOAuthOutcome
  googleError: unknown
  googlePending: boolean
  onGoogle: () => void
  onFix: () => void
  storage: string
  back: WizardReturn | undefined
}

/** The saved connection and what its test said. Saved first, tested second: a failed test never undoes the save. */
export function WizardResult({ created, google, ops, googleOutcome, googleError, googlePending, onGoogle, onFix, storage, back }: ResultProps) {
  useLanguage()
  const navigate = useNavigate()
  const test: ConnectionTestResponse['test'] | undefined = ops.test.data?.test
  const testing = ops.test.isPending
  const testError = Boolean(ops.test.error) && !testing
  const failed = test?.status === 'Failed' || testError
  const done = test?.status === 'Succeeded' || test?.status === 'NotTestable'
  const waiting = google && !test && !testing
  const leave = back ? { to: back.returnTo, state: { returnState: back.returnState } } : { to: storage, state: undefined }

  return <div className="space-y-4">
    {Boolean(googleError) && <ErrorNotice title={tx("Google yetkilendirmesi başlatılamadı")} error={googleError} scope="storageConnection" />}
    {google && googleOutcome.status === 'waiting' && <Notice tone="info" title={tx("Google penceresinde izin verin")}>{tx("İzin verildiğinde bağlantı otomatik test edilir.")}</Notice>}
    {google && googleOutcome.status === 'incomplete' && <Notice tone="warning" title={tx("Google hesabı henüz bağlanmadı")}>{tx("Bağlantı kaydedildi; izin verilene kadar listede “yetkilendirme gerekiyor” görünür. Google ile bağlan düğmesiyle yeniden deneyin. Google hesabının uygulamada test kullanıcısı olarak eklenmiş olması gerekir.")}</Notice>}
    {google && <GoogleOAuthNotice outcome={googleOutcome.status === 'connected' ? googleOutcome : { status: 'idle' }} />}
    {testing && <Notice tone="info" title={tx("Bağlantı test ediliyor…")}>{tx("Küçük bir test dosyası yazılıp okunur ve silinir; mevcut yedekleriniz değişmez.")}</Notice>}
    {test?.status === 'Succeeded' && <Notice tone="success" title={tx("Bağlantı çalışıyor")}>{testMessage(test)}</Notice>}
    {test?.status === 'NotTestable' && <Notice tone="info" title={tx("Bağlantı kaydedildi")}>{tx("Yedekleme bilgisayarı ilk yedekte denetler; sorun olursa orada görürsünüz.")}</Notice>}
    {failed && !google && <ErrorNotice title={tx("Test başarısız")} what={test ? testMessage(test) : tx("Test tamamlanamadı.")}
      dataImpact={tx("Bağlantı kaydedildi ama doğrulanmadı.")} action={tx("Bilgileri düzeltin ya da yeniden test edin.")} technical={test ? testTechnical(test) : []} />}
    {failed && google && <ErrorNotice title={tx("Test başarısız")} what={test ? testMessage(test) : tx("Test tamamlanamadı.")}
      dataImpact={tx("Bağlantı kaydedildi ama doğrulanmadı.")} action={tx("Google ile yeniden bağlanın.")} technical={test ? testTechnical(test) : []} />}
    <div className="flex flex-wrap gap-2">
      {done && (back
        ? <Button onClick={() => navigate(back.returnTo, { state: { returnState: back.returnState, storageConnectionId: created.id } })}>{tx("Bu depolamayı kullan")}</Button>
        : <Link to={storage} className={buttonVariants()}>{tx("Depolamaya dön")}</Link>)}
      {google && !testing && !done && <Button disabled={googlePending} onClick={onGoogle}><ExternalLink aria-hidden="true" className="size-4" />{googlePending ? tx('Yönlendiriliyor…') : tx('Google ile bağlan')}</Button>}
      {!google && failed && !testing && <Button onClick={onFix}>{tx("Bilgileri düzelt")}</Button>}
      {!google && failed && !testing && <Button variant="outline" onClick={() => ops.test.mutate(created)}><RefreshCw aria-hidden="true" className="size-4" />{tx("Yeniden test et")}</Button>}
      {(failed || waiting) && !done && <Link to={leave.to} state={leave.state} className={buttonVariants({ variant: 'ghost' })}>{back ? tx('Vazgeç') : tx('Depolamaya dön')}</Link>}
    </div>
  </div>
}
