import { tx, useLanguage } from '@/lib/i18n'
import { Advanced, FormGrid, PresetChoice, Question } from '@/features/backup-jobs/job-form-fields'
import { fieldKit } from '@/features/backup-jobs/job-field-kit'
import { keepForLabel, retentionPresets, schedulePresets } from '@/features/backup-jobs/job-wizard-model'
import type { JobFormState } from '@/features/backup-jobs/use-job-form'

/** Questions: when the job runs on its own and how long backups are kept. Cron, time zone, overlap, retry and exact retention seconds stay under the technical details. */
export function ScheduleStep({ form, advancedTitle }: { form: JobFormState; advancedTitle?: string }) {
  useLanguage()
  const { values, errors, canAdmin, nextRun, set } = form
  const { scheduled, schedule, overlap, retry, retention } = values
  const { text, number, select, check, withErrors } = fieldKit(errors)
  const customSchedule = scheduled && !schedulePresets.some(([, cron]) => cron === schedule.cronExpression)
  const customRetention = retention.enabled && !retentionPresets.some(([seconds]) => seconds === retention.keepForSeconds)
  const setSchedule = (next: typeof schedule) => set({ schedule: next })
  const setRetry = (next: typeof retry) => set({ retry: next })
  const setRetention = (next: typeof retention) => set({ retention: next })
  return <>
    <Question title={tx("Yedek ne zaman alınsın?")} why={tx("Düzenli yedek, bir sorun çıktığında kaybedeceğiniz veriyi sınırlar. Elle çalıştırma her zaman mümkündür.")}>
      <PresetChoice label={tx("Hazır zamanlamalar")} options={[
        ...schedulePresets.map(([label, cron]) => ({ text: tx(label), pressed: scheduled && schedule.cronExpression === cron, onSelect: () => set({ scheduled: true, schedule: { ...schedule, cronExpression: cron } }) })),
        { text: tx('Elle çalıştır'), pressed: !scheduled, onSelect: () => set({ scheduled: false }) },
      ]} />
      {customSchedule && <p className="mt-3 text-sm text-text-muted">{tx("Özel bir zamanlama kullanılıyor; ayrıntıları aşağıdaki Teknik ayrıntılar bölümünde görebilirsiniz.")}</p>}
      {nextRun && <p className="mt-3 text-sm text-text-muted">{tx("Kaydedilmiş zamanlamanın sonraki çalışması: {0}. Değişiklikler kaydedildikten sonra sunucu tarafından hesaplanır.", { '0': nextRun })}</p>}
    </Question>
    <Question title={tx("Yedekler ne kadar süre saklansın?")} why={tx("Süresi dolan yedekler silinir; böylece depolama alanı gereksiz yere dolmaz.")}>
      <FormGrid>
        {!canAdmin && <p className="text-sm text-text-muted md:col-span-2">{form.job ? tx('Mevcut politika korunur.') : tx('Sunucunun varsayılan politikası uygulanır.')}</p>}
        <PresetChoice label={tx("Saklama süresi")} disabled={!canAdmin} options={[
          ...retentionPresets.map(([seconds]) => ({ text: keepForLabel(seconds), pressed: retention.enabled && retention.keepForSeconds === seconds, onSelect: () => setRetention({ ...retention, enabled: true, keepForSeconds: seconds }) })),
          { text: tx('Süresiz'), pressed: !retention.enabled, onSelect: () => setRetention({ ...retention, enabled: false }) },
        ]} />
        {customRetention && <p className="text-sm text-text-muted md:col-span-2">{tx("Özel bir saklama süresi kullanılıyor ({0}); ayrıntıları aşağıdaki Teknik ayrıntılar bölümünde görebilirsiniz.", { '0': keepForLabel(retention.keepForSeconds) })}</p>}
        {form.job && <p className="text-sm text-text-muted md:col-span-2">{tx("Politika değişikliği mevcut yedeklerin son kullanma tarihini değiştirmez.")}</p>}
      </FormGrid>
    </Question>
    <Advanced title={advancedTitle} open={withErrors('cronExpression', 'timeZone', 'missedRunGraceSeconds', 'overlapPolicy', 'retryPolicy.maxAttempts', 'retryPolicy.initialDelaySeconds', 'retryPolicy.maxDelaySeconds', 'retryPolicy.backoff', 'retryPolicy.jitter', 'retention.keepForSeconds', 'retention.deleteGracePeriodSeconds')}>
      {scheduled && text('cronExpression', tx('Cron ifadesi'), schedule.cronExpression, cronExpression => setSchedule({ ...schedule, cronExpression }))}
      {scheduled && text('timeZone', tx('Saat dilimi (IANA / sistem adı)'), schedule.timeZone, timeZone => setSchedule({ ...schedule, timeZone }))}
      {scheduled && number('missedRunGraceSeconds', tx('Kaçırılan çalışma toleransı (saniye)'), schedule.missedRunGraceSeconds, value => setSchedule({ ...schedule, missedRunGraceSeconds: value ?? 0 }))}
      {scheduled && check(tx('Zamanlama etkin'), schedule.enabled, value => setSchedule({ ...schedule, enabled: value }))}
      {select('overlapPolicy', tx('Önceki çalışma sürerken yeni zamanlama'), overlap, [['Skip', tx('Atla')], ['Queue', tx('Kuyruğa al')]], value => set({ overlap: value as 'Skip' | 'Queue' }))}
      {number('retryPolicy.maxAttempts', tx('Toplam deneme sayısı'), retry.maxAttempts, value => setRetry({ ...retry, maxAttempts: value ?? 0 }))}
      {number('retryPolicy.initialDelaySeconds', tx('İlk gecikme (saniye)'), retry.initialDelaySeconds, value => setRetry({ ...retry, initialDelaySeconds: value ?? 0 }))}
      {number('retryPolicy.maxDelaySeconds', tx('En uzun gecikme (saniye)'), retry.maxDelaySeconds, value => setRetry({ ...retry, maxDelaySeconds: value ?? 0 }))}
      {select('retryPolicy.backoff', tx('Gecikme stratejisi'), retry.backoff, [['Fixed', tx('Fixed — sabit')], ['Exponential', tx('Exponential — üstel')]], backoff => setRetry({ ...retry, backoff }))}
      {number('retryPolicy.jitter', tx('Rastgele gecikme oranı (jitter)'), retry.jitter, value => setRetry({ ...retry, jitter: value ?? 0 }))}
      {number('retention.keepForSeconds', tx('KeepFor — saklama süresi (saniye)'), retention.keepForSeconds, value => setRetention({ ...retention, keepForSeconds: value ?? 0 }), !canAdmin)}
      {number('retention.deleteGracePeriodSeconds', tx('DeleteGracePeriod — silme bekleme süresi (saniye)'), retention.deleteGracePeriodSeconds, value => setRetention({ ...retention, deleteGracePeriodSeconds: value ?? 0 }), !canAdmin)}
    </Advanced>
  </>
}
