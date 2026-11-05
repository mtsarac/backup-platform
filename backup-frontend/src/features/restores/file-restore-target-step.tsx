import { CheckField } from '@/components/ui/check-field'
import { tx, useLanguage } from '@/lib/i18n'
import { RadioField } from '@/components/ui/radio-field'
import { TextInput } from '@/components/ui/text-input'
import { Button } from '@/components/ui/button'
import { Field } from '@/components/ui/field'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { Section } from '@/components/ui/section'
import type { RestoreTargetAgent, RestoreTargetsResponse } from './types'

interface FileRestoreTargetStepProps {
  instancePrefix: string
  targets: RestoreTargetsResponse
  originalAgent: RestoreTargetAgent | undefined
  original: boolean
  reference: string
  directory: string
  referenceValid: boolean
  directoryValid: boolean
  overwrite: boolean
  precheckError: unknown
  precheckRetryable: boolean
  precheckStarting: boolean
  restoreStarting: boolean
  canCheck: boolean
  onLocationChange: (location: 'original' | 'alternate') => void
  onReferenceChange: (value: string) => void
  onDirectoryChange: (value: string) => void
  onOverwriteChange: (overwrite: boolean) => void
  onBack: () => void
  onRunPrecheck: () => void
}

/** Where the files are put and what happens to files that already exist there. */
export function FileRestoreTargetStep({ instancePrefix, targets, originalAgent, original, reference, directory, referenceValid, directoryValid, overwrite, precheckError, precheckRetryable, precheckStarting, restoreStarting, canCheck, onLocationChange, onReferenceChange, onDirectoryChange, onOverwriteChange, onBack, onRunPrecheck }: FileRestoreTargetStepProps) {
  useLanguage()
  return (
    <div className="space-y-5">
      <Section title={tx("Dosyalar nereye geri konsun?")} description={tx("Neden soruluyor? Dosyalar ya yedeklendikleri yere geri döner ya da başka bir klasöre konur; ikincisinde mevcut dosyalara dokunulmaz.")}>
        <div className="space-y-4">
          <fieldset className="space-y-3">
            <legend className="sr-only">{tx("Geri yükleme konumu")}</legend>
            <div>
              <RadioField label={tx("Özgün konum")} name={`${instancePrefix}-location`} aria-describedby={`${instancePrefix}-original-hint`} checked={original} disabled={!originalAgent} onChange={() => onLocationChange('original')} />
              <p id={`${instancePrefix}-original-hint`} className="ml-6 text-sm text-text-muted">{tx("Dosyalar yedeklendikleri yere geri konur. Bunun için yedeği alan bilgisayar ve değişmemiş kaynak eşlemesi gerekir.")}</p>
            </div>
            <div>
              <RadioField label={tx("Alternatif konum")} name={`${instancePrefix}-location`} aria-describedby={`${instancePrefix}-alternate-hint`} checked={!original} onChange={() => onLocationChange('alternate')} />
              <p id={`${instancePrefix}-alternate-hint`} className="ml-6 text-sm text-text-muted">{tx("Dosyalar başka bir klasöre konur; yedeklendikleri yerdeki dosyalara dokunulmaz.")}</p>
            </div>
          </fieldset>
          {!originalAgent && <p className="text-sm text-text-muted">{tx("Özgün konum için kaynak işin Agent eşlemesi ve uygun bir Agent gerekir. Alternatif konum kullanabilirsiniz.")}</p>}
          {original ? <p className="text-sm">{tx("Dosya kaynağı referansı")}: <code>{targets.sourceDatabase}</code></p> : (
            <div className="grid gap-4 sm:grid-cols-2">
              <Field id={`${instancePrefix}-reference`} label={tx('Hedef dosya kaynağı referansı')} required error={!referenceValid ? tx('Geçerli bir dosya kaynağı referansı girin.') : undefined}>{(control) => <TextInput {...control} value={reference} onChange={(event) => onReferenceChange(event.target.value)} />}</Field>
              <Field id={`${instancePrefix}-directory`} label={tx('Hedef göreli klasör')} hint={tx('Boş değer veya . yapılandırılmış kökü kullanır; örnek: restored/2026.')} error={!directoryValid ? tx('Geçerli bir göreli yol girin.') : undefined}>{(control) => <TextInput {...control} value={directory} onChange={(event) => onDirectoryChange(event.target.value)} />}</Field>
            </div>
          )}
        </div>
      </Section>

      <Section title={tx("Aynı adlı dosyalar ne olsun?")} description={tx("Neden soruluyor? Hedefte aynı adlı bir dosya varsa onun korunup korunmayacağını sizin belirlemeniz gerekir.")}>
        <div className="space-y-3">
          <CheckField label={tx("Mevcut dosyaların üzerine yazmaya izin ver")} aria-describedby={`${instancePrefix}-overwrite-hint`} checked={overwrite} onChange={(event) => onOverwriteChange(event.target.checked)} />
          <p id={`${instancePrefix}-overwrite-hint`} className="ml-6 text-sm text-text-muted">{tx("İşaretlemezseniz hedefteki mevcut dosyaların üzerine yazılmaz. Dosya izinleri, zaman damgaları ve bütünlük korunur.")}</p>
          {overwrite && <Notice tone="warning" title={tx("Veri kaybı riski")}>
            <p>{tx("Seçili hedefteki çakışan dosyalar değiştirilecektir. Başlatmadan önce kaynak referansını, göreli klasörü ve kapsamı kontrol edin.")}</p>
            <p className="mt-1">{tx("Başlatmadan önce hedef dosya kaynağı referansını yazarak onaylamanız istenecek.")}</p>
          </Notice>}
        </div>
      </Section>

      {precheckError != null && <ErrorNotice error={precheckError} scope="restore" />}

      <div className="flex flex-wrap justify-between gap-3">
        <Button variant="outline" onClick={() => onBack()}>{tx("← Geri")}</Button>
        <Button disabled={!canCheck || precheckStarting || restoreStarting} onClick={() => onRunPrecheck()}>
          {precheckStarting ? tx('Ön Kontrol Başlatılıyor…') : precheckRetryable ? tx('Tekrar Dene') : tx('Ön Kontrolü Çalıştır →')}
        </Button>
      </div>
    </div>
  )
}
