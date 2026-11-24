import type { ReactNode } from 'react'
import { CheckField } from '@/components/ui/check-field'
import { tx, useLanguage } from '@/lib/i18n'
import { RadioField } from '@/components/ui/radio-field'
import { Button } from '@/components/ui/button'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { Section } from '@/components/ui/section'
import { describeError } from '@/lib/api/failure-messages'
import type { RestoreTargetAgent } from './types'

interface FileRestoreTargetStepProps {
  instancePrefix: string
  originalAgent: RestoreTargetAgent | undefined
  original: boolean
  overwrite: boolean
  precheckError: unknown
  precheckRetryable: boolean
  precheckStarting: boolean
  restoreStarting: boolean
  canCheck: boolean
  checkPanel: ReactNode
  onLocationChange: (location: 'original' | 'alternate') => void
  onOverwriteChange: (overwrite: boolean) => void
  onBack: () => void
  onContinue: () => void
}

/** Where the files are put and what happens to files that already exist there. */
export function FileRestoreTargetStep({ instancePrefix, originalAgent, original, overwrite, precheckError, precheckRetryable, precheckStarting, restoreStarting, canCheck, checkPanel, onLocationChange, onOverwriteChange, onBack, onContinue }: FileRestoreTargetStepProps) {
  useLanguage()
  return (
    <div className="space-y-5">
      <Section title={tx("Dosyalar nereye geri konsun?")} description={tx("Neden soruluyor? Dosyalar ya yedeklendikleri yere geri döner ya da ayrı bir klasöre konur; ikincisinde mevcut dosyalara dokunulmaz.")}>
        <div className="space-y-4">
          <fieldset className="space-y-3">
            <legend className="sr-only">{tx("Geri yükleme konumu")}</legend>
            <div>
              <RadioField label={tx("Özgün konum")} name={`${instancePrefix}-location`} aria-describedby={`${instancePrefix}-original-hint`} checked={original} disabled={!originalAgent} onChange={() => onLocationChange('original')} />
              <p id={`${instancePrefix}-original-hint`} className="ml-6 text-sm text-text-muted">{tx("Dosyalar yedeklendikleri yere geri konur. Bunun için yedeği alan bilgisayar ve değişmemiş kaynak eşlemesi gerekir.")}</p>
            </div>
            <div>
              <RadioField label={tx("Alternatif konum")} name={`${instancePrefix}-location`} aria-describedby={`${instancePrefix}-alternate-hint`} checked={!original} onChange={() => onLocationChange('alternate')} />
              <p id={`${instancePrefix}-alternate-hint`} className="ml-6 text-sm text-text-muted">{tx('Dosyalar ayrı bir klasöre (restored) konur; yedeklendikleri yerdeki dosyalara dokunulmaz.')}</p>
            </div>
          </fieldset>
          {!originalAgent && <p className="text-sm text-text-muted">{tx("Özgün konum için kaynak işin Agent eşlemesi ve uygun bir Agent gerekir. Alternatif konum kullanabilirsiniz.")}</p>}
        </div>
      </Section>

      <Section title={tx("Aynı adlı dosyalar ne olsun?")} description={tx("Neden soruluyor? Hedefte aynı adlı bir dosya varsa onun korunup korunmayacağını sizin belirlemeniz gerekir.")}>
        <div className="space-y-3">
          <CheckField label={tx("Mevcut dosyaların üzerine yazmaya izin ver")} aria-describedby={`${instancePrefix}-overwrite-hint`} checked={overwrite} onChange={(event) => onOverwriteChange(event.target.checked)} />
          <p id={`${instancePrefix}-overwrite-hint`} className="ml-6 text-sm text-text-muted">{tx("İşaretlemezseniz hedefteki mevcut dosyaların üzerine yazılmaz. Dosya izinleri, zaman damgaları ve bütünlük korunur.")}</p>
          {overwrite && <Notice tone="warning" title={tx("Veri kaybı riski")}>
            <p>{tx("Hedefteki çakışan dosyalar değiştirilecektir. Başlatmadan önce konumu ve kapsamı kontrol edin.")}</p>
            <p className="mt-1">{tx("Başlatmadan önce adı yazarak onaylamanız istenecek.")}</p>
          </Notice>}
        </div>
      </Section>

      {checkPanel}
      {precheckError != null && <ErrorNotice error={precheckError} scope="restore" onRetry={describeError(precheckError, 'restore').actionKind === 'retry' && canCheck && !precheckStarting && !restoreStarting ? onContinue : undefined} />}

      <div className="flex flex-wrap justify-between gap-3">
        <Button variant="outline" onClick={() => onBack()}>{tx("← Geri")}</Button>
        <Button disabled={!canCheck || precheckStarting || restoreStarting} onClick={() => onContinue()}>
          {precheckStarting ? tx('Ön kontrol çalışıyor…') : precheckRetryable ? tx('Tekrar dene') : tx('Devam et →')}
        </Button>
      </div>
    </div>
  )
}
