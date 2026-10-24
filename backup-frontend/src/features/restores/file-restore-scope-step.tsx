import { tx, useLanguage } from '@/lib/i18n'
import { Button } from '@/components/ui/button'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { Section } from '@/components/ui/section'
import { FilePathList } from '@/features/backup-jobs/file-path-list'

interface FileRestoreScopeStepProps {
  instancePrefix: string
  scope: 'all' | 'selected'
  paths: string[]
  pathsValid: boolean
  overwrite: boolean
  precheckError: unknown
  precheckRetryable: boolean
  precheckStarting: boolean
  restoreStarting: boolean
  canCheck: boolean
  onScopeChange: (scope: 'all' | 'selected') => void
  onPathsChange: (paths: string[]) => void
  onOverwriteChange: (overwrite: boolean) => void
  onBack: () => void
  onRunPrecheck: () => void
}

export function FileRestoreScopeStep({ instancePrefix, scope, paths, pathsValid, overwrite, precheckError, precheckRetryable, precheckStarting, restoreStarting, canCheck, onScopeChange, onPathsChange, onOverwriteChange, onBack, onRunPrecheck }: FileRestoreScopeStepProps) {
  useLanguage()
  return (
    <div className="space-y-5">
      <Section title={tx("Hangi dosyalar geri yüklensin?")} description={tx("Neden soruluyor? Yedeğin tamamını ya da yalnızca ihtiyacınız olan dosya ve klasörleri geri yükleyebilirsiniz.")}>
        <div className="space-y-4">
          <fieldset className="flex flex-wrap gap-4">
            <legend className="sr-only">{tx("Geri yükleme kapsamı")}</legend>
            <label className="flex items-center gap-2 text-sm"><input type="radio" name={`${instancePrefix}-scope`} checked={scope === 'all'} onChange={() => onScopeChange('all')} />{tx("Tüm dosya ve klasörler")}</label>
            <label className="flex items-center gap-2 text-sm"><input type="radio" name={`${instancePrefix}-scope`} checked={scope === 'selected'} onChange={() => onScopeChange('selected')} />{tx("Seçili dosya ve klasörler")}</label>
          </fieldset>
          {scope === 'selected' && <FilePathList id={`${instancePrefix}-paths`} label={tx('Geri yüklenecek göreli yollar')} paths={paths} onChange={onPathsChange} error={!pathsValid ? tx('1–256 geçerli göreli yol girin.') : undefined} />}
          <p className="text-sm text-text-muted">{tx("Seçilen klasörlerin altındaki dosyalar da geri yüklenir; arşivdeki göreli dizin yapısı korunur. Arşivde bulunmayan seçimler işlemi durdurur.")}</p>
        </div>
      </Section>

      <Section title={tx("Aynı adlı dosyalar ne olsun?")} description={tx("Neden soruluyor? Hedefte aynı adlı bir dosya varsa onun korunup korunmayacağını sizin belirlemeniz gerekir.")}>
        <div className="space-y-3">
          <label className="flex items-start gap-2 text-sm">
            <input type="checkbox" className="mt-1" aria-describedby={`${instancePrefix}-overwrite-hint`} checked={overwrite} onChange={(event) => onOverwriteChange(event.target.checked)} />
            <span>{tx("Mevcut dosyaların üzerine yazmaya izin ver")}</span>
          </label>
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
