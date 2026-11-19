import { tx, useLanguage } from '@/lib/i18n'
import { FormGrid, PresetChoice, Question } from '@/features/backup-jobs/job-form-fields'
import { keepForLabel, retentionPresets, schedulePresets } from '@/features/backup-jobs/job-wizard-model'
import type { JobFormState } from '@/features/backup-jobs/use-job-form'
import { Field } from '@/components/ui/field'
import { Select } from '@/components/ui/text-input'

type Props = { form: JobFormState; headingLevel?: 2 | 3 }

/** Question: when the job runs on its own; cron, time zone, overlap, retry and grace values are pinned, not asked. An existing job keeps its saved time zone. */
export function ScheduleQuestion({ form, headingLevel }: Props) {
  useLanguage()
  const { values, saved, nextRun, set } = form
  const { scheduled, schedule } = values
  const customSaved = saved.scheduled && !schedulePresets.some(([, cron]) => cron === saved.schedule.cronExpression)
  return <Question compact={Boolean(form.job)} headingLevel={headingLevel} title={tx("Yedek ne zaman alınsın?")} why={tx("Düzenli yedek, bir sorun çıktığında kaybedeceğiniz veriyi sınırlar. Elle çalıştırma her zaman mümkündür.")}>
    {form.job ? <Field label={tx("Hazır zamanlamalar")}>{control => <Select {...control} value={!scheduled ? 'manual' : schedule.cronExpression === saved.schedule.cronExpression && customSaved ? 'custom' : schedule.cronExpression} onChange={event => {
      const cronExpression = event.target.value
      if (cronExpression === 'manual') set({ scheduled: false })
      else if (cronExpression === 'custom') set({ scheduled: true, schedule: saved.schedule })
      else set({ scheduled: true, schedule: { ...schedule, cronExpression } })
    }}>
      {schedulePresets.map(([label, cron]) => <option key={cron} value={cron}>{tx(label)}</option>)}
      <option value="manual">{tx('Elle çalıştır')}</option>
      {customSaved && <option value="custom">{tx('Özel zamanlama (mevcut)')}</option>}
    </Select>}</Field> : <PresetChoice label={tx("Hazır zamanlamalar")} options={[
      ...schedulePresets.map(([label, cron]) => ({ text: tx(label), pressed: scheduled && schedule.cronExpression === cron, onSelect: () => set({ scheduled: true, schedule: { ...schedule, cronExpression: cron } }) })),
      { text: tx('Elle çalıştır'), pressed: !scheduled, onSelect: () => set({ scheduled: false }) },
    ]} />}
    {nextRun && <p className="mt-3 text-sm text-text-muted">{tx("Kaydedilmiş zamanlamanın sonraki çalışması: {0}. Değişiklikler kaydedildikten sonra sunucu tarafından hesaplanır.", { '0': nextRun })}</p>}
  </Question>
}

/** Question: how long backups are kept; only an administrator changes it. */
export function RetentionQuestion({ form, headingLevel }: Props) {
  useLanguage()
  const { saved, values, set } = form
  const { retention } = values
  const customSaved = saved.retention.enabled && !retentionPresets.some(([seconds]) => seconds === saved.retention.keepForSeconds)
  const setRetention = (next: typeof retention) => set({ retention: next })
  return <Question compact={Boolean(form.job)} headingLevel={headingLevel} title={tx("Yedekler ne kadar süre saklansın?")} why={tx("Süresi dolan yedekler silinir; böylece depolama alanı gereksiz yere dolmaz.")}>
    <FormGrid>
      {form.job ? <Field label={tx("Saklama süresi")}>{control => <Select {...control} value={!retention.enabled ? 'off' : customSaved && retention.keepForSeconds === saved.retention.keepForSeconds ? 'custom' : String(retention.keepForSeconds)} onChange={event => {
        const value = event.target.value
        if (value === 'off') setRetention({ ...retention, enabled: false })
        else if (value === 'custom') setRetention(saved.retention)
        else setRetention({ ...retention, enabled: true, keepForSeconds: Number(value) })
      }}>
        {retentionPresets.map(([seconds]) => <option key={seconds} value={seconds}>{keepForLabel(seconds)}</option>)}
        <option value="off">{tx('Hiç silinmesin')}</option>
        {customSaved && <option value="custom">{tx('{0} (mevcut)', { '0': keepForLabel(saved.retention.keepForSeconds) })}</option>}
      </Select>}</Field> : <PresetChoice label={tx("Saklama süresi")} options={[
        ...retentionPresets.map(([seconds]) => ({ text: keepForLabel(seconds), pressed: retention.enabled && retention.keepForSeconds === seconds, onSelect: () => setRetention({ ...retention, enabled: true, keepForSeconds: seconds }) })),
        { text: tx('Hiç silinmesin'), pressed: !retention.enabled, onSelect: () => setRetention({ ...retention, enabled: false }) },
      ]} />}
      {form.job && JSON.stringify(retention) !== JSON.stringify(saved.retention) && <p className="text-sm text-text-muted md:col-span-2">{tx("Yeni süre yalnızca bundan sonraki yedeklere uygulanır; mevcut yedeklerin süresi değişmez.")}</p>}
    </FormGrid>
  </Question>
}

/** The create wizard asks both on one step. */
export function ScheduleStep(props: Props) {
  return <><ScheduleQuestion {...props} /><RetentionQuestion {...props} /></>
}
