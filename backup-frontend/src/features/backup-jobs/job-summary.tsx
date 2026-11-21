import { tx, useLanguage } from '@/lib/i18n'
import { Button } from '@/components/ui/button'
import { Question } from '@/features/backup-jobs/job-form-fields'
import { summaryRows, type SummaryInput } from '@/features/backup-jobs/job-summary-rows'
import type { EditSection } from '@/features/backup-jobs/job-wizard-model'

/** Plain-language review of everything the wizard will create. */
export function JobSummary(props: SummaryInput & { onGo: (step: EditSection) => void; onAddStorage?: () => void }) {
  useLanguage()
  // The change button sits in a second `dd` so every `dl` child stays a valid term/definition group.
  return <Question title={tx("Her şey doğru mu?")} why={tx("Kaydet'e basmadan önce neyin oluşturulacağını son kez görürsünüz; hiçbir şey o ana kadar değişmez.")}>
    <dl>
      {summaryRows(props).map(({ label, step, value }) => <div key={label} className="grid grid-cols-[minmax(0,1fr)_auto] items-start gap-x-3 border-b border-border py-3 last:border-b-0">
        <dt className="col-start-1 text-sm text-text-muted">{label}</dt><dd className="col-start-1 mt-0.5 break-words text-body text-text">{value}{step === 'storage' && props.onAddStorage && <Button type="button" variant="outline" size="sm" className="mt-2 block" onClick={props.onAddStorage}>{tx("Yeni depolama ekle")}</Button>}</dd>
        <dd className="col-start-2 row-span-2 row-start-1"><Button type="button" variant="outline" size="sm" aria-label={tx("Değiştir: {0}", { "0": label })} onClick={() => props.onGo(step)}>{tx("Değiştir")}</Button></dd>
      </div>)}
    </dl>
  </Question>
}
