import { tx, useLanguage } from '@/lib/i18n'
import { Button } from '@/components/ui/button'
import { Section } from '@/components/ui/section'
import { Advanced, FormGrid } from '@/features/backup-jobs/job-form-fields'
import { fieldKit, type FieldErrors } from '@/features/backup-jobs/job-field-kit'
import { schedulePresets } from '@/features/backup-jobs/job-wizard-model'
import type { RetryPolicy, Schedule } from '@/features/backup-jobs/types'

interface ScheduleProps { scheduled: boolean; onScheduled: (scheduled: boolean) => void; schedule: Schedule; onSchedule: (schedule: Schedule) => void; nextRun?: string }

/** Question group: whether and when the job runs on its own. */
export function ScheduleGroup({ scheduled, onScheduled, schedule, onSchedule, nextRun }: ScheduleProps) {
  useLanguage()
  const { check } = fieldKit({})
  return <Section title={tx("Zamanlama")} description={tx("Manuel çalıştırma her zaman mümkündür; zamanlama isteğe bağlıdır.")}>
    <FormGrid>
      {check(tx('Zamanlama kullan'), scheduled, onScheduled)}
      {scheduled && check(tx('Zamanlama etkin'), schedule.enabled, value => onSchedule({ ...schedule, enabled: value }))}
      {scheduled && <div role="group" aria-label={tx("Hazır zamanlamalar")} className="flex flex-wrap gap-2 md:col-span-2">{schedulePresets.map(([label, cron]) => <Button key={cron} type="button" variant={schedule.cronExpression === cron ? 'default' : 'outline'} aria-pressed={schedule.cronExpression === cron} onClick={() => onSchedule({ ...schedule, cronExpression: cron })}>{tx(label)}</Button>)}</div>}
      {nextRun && <p className="text-sm text-text-muted md:col-span-2">{tx("Kaydedilmiş zamanlamanın sonraki çalışması:")}{' '}{nextRun}{tx(". Değişiklikler kaydedildikten sonra sunucu tarafından hesaplanır.")}</p>}
    </FormGrid>
  </Section>
}

interface AdvancedProps { errors: FieldErrors; scheduled: boolean; schedule: Schedule; onSchedule: (schedule: Schedule) => void; overlap: 'Skip' | 'Queue'; onOverlap: (overlap: 'Skip' | 'Queue') => void }

/** Technical question group: cron expression, time zone, missed-run tolerance, overlap. */
export function AdvancedScheduleGroup({ errors, scheduled, schedule, onSchedule, overlap, onOverlap }: AdvancedProps) {
  useLanguage()
  const { text, number, select, withErrors } = fieldKit(errors)
  return <Advanced title={tx("Gelişmiş zamanlama")} open={withErrors('cronExpression', 'timeZone', 'missedRunGraceSeconds', 'overlapPolicy')}>
    {scheduled && text('cronExpression', tx('Cron ifadesi'), schedule.cronExpression, cronExpression => onSchedule({ ...schedule, cronExpression }))}
    {scheduled && text('timeZone', tx('Saat dilimi (IANA / sistem adı)'), schedule.timeZone, timeZone => onSchedule({ ...schedule, timeZone }))}
    {scheduled && number('missedRunGraceSeconds', tx('Kaçırılan çalışma toleransı (saniye)'), schedule.missedRunGraceSeconds, value => onSchedule({ ...schedule, missedRunGraceSeconds: value ?? 0 }))}
    {select('overlapPolicy', tx('Önceki çalışma sürerken yeni zamanlama'), overlap, [['Skip', tx('Atla')], ['Queue', tx('Kuyruğa al')]], value => onOverlap(value as 'Skip' | 'Queue'))}
  </Advanced>
}

interface RetryProps { errors: FieldErrors; retry: RetryPolicy; onRetry: (retry: RetryPolicy) => void }

/** Technical question group: how failed runs are retried. */
export function RetryGroup({ errors, retry, onRetry }: RetryProps) {
  useLanguage()
  const { number, select, withErrors } = fieldKit(errors)
  return <Advanced title={tx("Yeniden deneme")} open={withErrors('retryPolicy.maxAttempts', 'retryPolicy.initialDelaySeconds', 'retryPolicy.maxDelaySeconds', 'retryPolicy.backoff', 'retryPolicy.jitter')}>
    {number('retryPolicy.maxAttempts', tx('Toplam deneme sayısı'), retry.maxAttempts, value => onRetry({ ...retry, maxAttempts: value ?? 0 }))}
    {number('retryPolicy.initialDelaySeconds', tx('İlk gecikme (saniye)'), retry.initialDelaySeconds, value => onRetry({ ...retry, initialDelaySeconds: value ?? 0 }))}
    {number('retryPolicy.maxDelaySeconds', tx('En uzun gecikme (saniye)'), retry.maxDelaySeconds, value => onRetry({ ...retry, maxDelaySeconds: value ?? 0 }))}
    {select('retryPolicy.backoff', tx('Gecikme stratejisi'), retry.backoff, [['Fixed', tx('Fixed — sabit')], ['Exponential', tx('Exponential — üstel')]], backoff => onRetry({ ...retry, backoff }))}
    {number('retryPolicy.jitter', tx('Rastgele gecikme oranı (jitter)'), retry.jitter, value => onRetry({ ...retry, jitter: value ?? 0 }))}
  </Advanced>
}

export function ScheduleStep(props: ScheduleProps & AdvancedProps & RetryProps) {
  return <>
    <ScheduleGroup {...props} />
    <AdvancedScheduleGroup {...props} />
    <RetryGroup {...props} />
  </>
}
