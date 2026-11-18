import { tx, useLanguage } from '@/lib/i18n'
import { FormGrid, PresetChoice, Question } from '@/features/backup-jobs/job-form-fields'
import { keepForLabel, retentionPresets, schedulePresets } from '@/features/backup-jobs/job-wizard-model'
import type { JobFormState } from '@/features/backup-jobs/use-job-form'
import { Field } from '@/components/ui/field'
import { Select } from '@/components/ui/text-input'

/** Questions: when the job runs on its own and how long backups are kept; cron, time zone, overlap, retry and grace values are pinned, not asked. */
export function ScheduleStep({ form, headingLevel }: { form: JobFormState; headingLevel?: 2 | 3 }) {
  useLanguage()
  const { values, canAdmin, nextRun, set } = form
  const { scheduled, schedule, retention } = values
  const customSchedule = scheduled && !schedulePresets.some(([, cron]) => cron === schedule.cronExpression)
  const customRetention = retention.enabled && !retentionPresets.some(([seconds]) => seconds === retention.keepForSeconds)
  const setRetention = (next: typeof retention) => set({ retention: next })
  return <>
    <Question compact={Boolean(form.job)} headingLevel={headingLevel} title={tx("Yedek ne zaman alınsın?")} why={tx("Düzenli yedek, bir sorun çıktığında kaybedeceğiniz veriyi sınırlar. Elle çalıştırma her zaman mümkündür.")}>
      {form.job ? <Field label={tx("Hazır zamanlamalar")}>{control => <Select {...control} value={!scheduled ? 'manual' : customSchedule ? 'custom' : schedule.cronExpression} onChange={event => {
        const cronExpression = event.target.value
        if (cronExpression === 'manual') set({ scheduled: false })
        else if (cronExpression !== 'custom') set({ scheduled: true, schedule: { ...schedule, cronExpression } })
      }}>
        {schedulePresets.map(([label, cron]) => <option key={cron} value={cron}>{tx(label)}</option>)}
        <option value="manual">{tx('Elle çalıştır')}</option>
        {customSchedule && <option value="custom">{tx('Özel zamanlama')}</option>}
      </Select>}</Field> : <PresetChoice label={tx("Hazır zamanlamalar")} options={[
        ...schedulePresets.map(([label, cron]) => ({ text: tx(label), pressed: scheduled && schedule.cronExpression === cron, onSelect: () => set({ scheduled: true, schedule: { ...schedule, cronExpression: cron } }) })),
        { text: tx('Elle çalıştır'), pressed: !scheduled, onSelect: () => set({ scheduled: false }) },
      ]} />}
      {customSchedule && <p className="mt-3 text-sm text-text-muted">{tx("Özel bir zamanlama kullanılıyor; ayrıntıları aşağıdaki Teknik ayrıntılar bölümünde görebilirsiniz.")}</p>}
      {nextRun && <p className="mt-3 text-sm text-text-muted">{tx("Kaydedilmiş zamanlamanın sonraki çalışması: {0}. Değişiklikler kaydedildikten sonra sunucu tarafından hesaplanır.", { '0': nextRun })}</p>}
    </Question>
    <Question compact={Boolean(form.job)} headingLevel={headingLevel} title={tx("Yedekler ne kadar süre saklansın?")} why={tx("Süresi dolan yedekler silinir; böylece depolama alanı gereksiz yere dolmaz.")}>
      <FormGrid>
        {!canAdmin && <p className="text-sm text-text-muted md:col-span-2">{form.job ? tx('Mevcut politika korunur.') : tx('Sunucunun varsayılan politikası uygulanır.')}</p>}
        {form.job ? <Field label={tx("Saklama süresi")}>{control => <Select {...control} disabled={!canAdmin} value={!retention.enabled ? 'off' : customRetention ? 'custom' : String(retention.keepForSeconds)} onChange={event => {
          const value = event.target.value
          if (value === 'off') setRetention({ ...retention, enabled: false })
          else if (value !== 'custom') setRetention({ ...retention, enabled: true, keepForSeconds: Number(value) })
        }}>
          {retentionPresets.map(([seconds]) => <option key={seconds} value={seconds}>{keepForLabel(seconds)}</option>)}
          <option value="off">{tx('Süresiz')}</option>
          {customRetention && <option value="custom">{tx('{0} saniye', { '0': retention.keepForSeconds })}</option>}
        </Select>}</Field> : <PresetChoice label={tx("Saklama süresi")} disabled={!canAdmin} options={[
          ...retentionPresets.map(([seconds]) => ({ text: keepForLabel(seconds), pressed: retention.enabled && retention.keepForSeconds === seconds, onSelect: () => setRetention({ ...retention, enabled: true, keepForSeconds: seconds }) })),
          { text: tx('Hiç silinmesin'), pressed: !retention.enabled, onSelect: () => setRetention({ ...retention, enabled: false }) },
        ]} />}
        {customRetention && <p className="text-sm text-text-muted md:col-span-2">{tx("Özel bir saklama süresi kullanılıyor ({0}); ayrıntıları aşağıdaki Teknik ayrıntılar bölümünde görebilirsiniz.", { '0': form.job ? tx('{0} saniye', { '0': retention.keepForSeconds }) : keepForLabel(retention.keepForSeconds) })}</p>}
        {form.job && <p className="text-sm text-text-muted md:col-span-2">{tx("Politika değişikliği mevcut yedeklerin son kullanma tarihini değiştirmez.")}</p>}
      </FormGrid>
    </Question>
  </>
}
