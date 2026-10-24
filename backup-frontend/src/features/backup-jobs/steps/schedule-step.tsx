import { tx, useLanguage } from '@/lib/i18n'
import { Advanced, Question, PresetChoice } from '@/features/backup-jobs/job-form-fields'
import { fieldKit, type FieldErrors } from '@/features/backup-jobs/job-field-kit'
import { schedulePresets } from '@/features/backup-jobs/job-wizard-model'
import type { RetryPolicy, Schedule } from '@/features/backup-jobs/types'

interface Props {
  errors: FieldErrors; scheduled: boolean; onScheduled: (scheduled: boolean) => void; schedule: Schedule; onSchedule: (schedule: Schedule) => void; nextRun?: string
  overlap: 'Skip' | 'Queue'; onOverlap: (overlap: 'Skip' | 'Queue') => void; retry: RetryPolicy; onRetry: (retry: RetryPolicy) => void
}

/** Question: when the job runs on its own. Cron, time zone, overlap and retry stay under the technical details. */
export function ScheduleStep({ errors, scheduled, onScheduled, schedule, onSchedule, nextRun, overlap, onOverlap, retry, onRetry }: Props) {
  useLanguage()
  const { text, number, select, check, withErrors } = fieldKit(errors)
  const custom = scheduled && !schedulePresets.some(([, cron]) => cron === schedule.cronExpression)
  return <>
    <Question title={tx("Yedek ne zaman alınsın?")} why={tx("Düzenli yedek, bir sorun çıktığında kaybedeceğiniz veriyi sınırlar. Elle çalıştırma her zaman mümkündür.")}>
      <PresetChoice label={tx("Hazır zamanlamalar")} options={[
        ...schedulePresets.map(([label, cron]) => ({ text: tx(label), pressed: scheduled && schedule.cronExpression === cron, onSelect: () => { onScheduled(true); onSchedule({ ...schedule, cronExpression: cron }) } })),
        { text: tx('Elle çalıştır'), pressed: !scheduled, onSelect: () => onScheduled(false) },
      ]} />
      {custom && <p className="mt-3 text-sm text-text-muted">{tx("Özel bir zamanlama kullanılıyor; ayrıntıları aşağıdaki Teknik ayrıntılar bölümünde görebilirsiniz.")}</p>}
      {nextRun && <p className="mt-3 text-sm text-text-muted">{tx("Kaydedilmiş zamanlamanın sonraki çalışması: {0}. Değişiklikler kaydedildikten sonra sunucu tarafından hesaplanır.", { '0': nextRun })}</p>}
    </Question>
    <Advanced open={withErrors('cronExpression', 'timeZone', 'missedRunGraceSeconds', 'overlapPolicy', 'retryPolicy.maxAttempts', 'retryPolicy.initialDelaySeconds', 'retryPolicy.maxDelaySeconds', 'retryPolicy.backoff', 'retryPolicy.jitter')}>
      {scheduled && text('cronExpression', tx('Cron ifadesi'), schedule.cronExpression, cronExpression => onSchedule({ ...schedule, cronExpression }))}
      {scheduled && text('timeZone', tx('Saat dilimi (IANA / sistem adı)'), schedule.timeZone, timeZone => onSchedule({ ...schedule, timeZone }))}
      {scheduled && number('missedRunGraceSeconds', tx('Kaçırılan çalışma toleransı (saniye)'), schedule.missedRunGraceSeconds, value => onSchedule({ ...schedule, missedRunGraceSeconds: value ?? 0 }))}
      {scheduled && check(tx('Zamanlama etkin'), schedule.enabled, value => onSchedule({ ...schedule, enabled: value }))}
      {select('overlapPolicy', tx('Önceki çalışma sürerken yeni zamanlama'), overlap, [['Skip', tx('Atla')], ['Queue', tx('Kuyruğa al')]], value => onOverlap(value as 'Skip' | 'Queue'))}
      {number('retryPolicy.maxAttempts', tx('Toplam deneme sayısı'), retry.maxAttempts, value => onRetry({ ...retry, maxAttempts: value ?? 0 }))}
      {number('retryPolicy.initialDelaySeconds', tx('İlk gecikme (saniye)'), retry.initialDelaySeconds, value => onRetry({ ...retry, initialDelaySeconds: value ?? 0 }))}
      {number('retryPolicy.maxDelaySeconds', tx('En uzun gecikme (saniye)'), retry.maxDelaySeconds, value => onRetry({ ...retry, maxDelaySeconds: value ?? 0 }))}
      {select('retryPolicy.backoff', tx('Gecikme stratejisi'), retry.backoff, [['Fixed', tx('Fixed — sabit')], ['Exponential', tx('Exponential — üstel')]], backoff => onRetry({ ...retry, backoff }))}
      {number('retryPolicy.jitter', tx('Rastgele gecikme oranı (jitter)'), retry.jitter, value => onRetry({ ...retry, jitter: value ?? 0 }))}
    </Advanced>
  </>
}
