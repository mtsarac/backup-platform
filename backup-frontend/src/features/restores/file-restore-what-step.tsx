import { tx, useLanguage } from '@/lib/i18n'
import { Why } from '@/features/backup-jobs/job-form-fields'
import { RadioField } from '@/components/ui/radio-field'
import { Button } from '@/components/ui/button'
import { Section } from '@/components/ui/section'
import { FilePathList } from '@/features/backup-jobs/file-path-list'

interface FileRestoreWhatStepProps {
  instancePrefix: string
  scope: 'all' | 'selected'
  paths: string[]
  pathsValid: boolean
  onScopeChange: (scope: 'all' | 'selected') => void
  onPathsChange: (paths: string[]) => void
  onBack?: () => void
  onContinue: () => void
}

/** Which of the backed-up files come back: everything, or the listed paths. */
export function FileRestoreWhatStep({ instancePrefix, scope, paths, pathsValid, onScopeChange, onPathsChange, onBack, onContinue }: FileRestoreWhatStepProps) {
  useLanguage()
  return (
    <div className="space-y-5">
      <Section title={tx("Hangi dosyalar geri yüklensin?")} description={<Why>{tx("Yedeğin tamamını ya da yalnızca ihtiyacınız olan dosya ve klasörleri geri yükleyebilirsiniz.")}</Why>}>
        <div className="space-y-4">
          <fieldset className="flex flex-wrap gap-4">
            <legend className="sr-only">{tx("Geri yükleme kapsamı")}</legend>
            <RadioField label={tx("Tüm dosya ve klasörler")} name={`${instancePrefix}-scope`} checked={scope === 'all'} onChange={() => onScopeChange('all')} />
            <RadioField label={tx("Seçili dosya ve klasörler")} name={`${instancePrefix}-scope`} checked={scope === 'selected'} onChange={() => onScopeChange('selected')} />
          </fieldset>
          {scope === 'selected' && <FilePathList id={`${instancePrefix}-paths`} label={tx('Geri yüklenecek göreli yollar')} paths={paths} onChange={onPathsChange} error={!pathsValid ? tx('1–256 geçerli göreli yol girin.') : undefined} />}
          <p className="text-sm text-text-muted">{tx("Seçilen klasörlerin altındaki dosyalar da geri yüklenir; arşivdeki göreli dizin yapısı korunur. Arşivde bulunmayan seçimler işlemi durdurur.")}</p>
        </div>
      </Section>

      <div className="flex flex-wrap justify-between gap-3">
        {onBack ? <Button variant="outline" onClick={() => onBack()}>{tx("← Geri")}</Button> : <span />}
        <Button disabled={!pathsValid} onClick={() => onContinue()}>{tx("Devam et →")}</Button>
      </div>
    </div>
  )
}
