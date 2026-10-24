import { tx, useLanguage } from '@/lib/i18n'
import { TechnicalDetails, type TechnicalItem } from '@/components/ui/technical-details'
import { policyLabels } from '@/features/artifact-encryption/labels'
import { schedulePresetLabel } from '@/features/backup-jobs/job-wizard-model'
import type { BackupJob } from '@/features/backup-jobs/types'
import { formatDateTime } from '@/lib/format'

/** Schedule, encryption and identifiers of one job: the list leaves them out, so they live here, collapsed and copyable. */
export function JobDetails({ job }: { job: BackupJob }) {
  useLanguage()
  const { schedule } = job
  const label = schedule ? schedulePresetLabel(schedule.cronExpression) ?? tx('Özel zamanlama') : tx('Manuel')
  const next = job.nextOccurrenceAt ?? schedule?.nextOccurrenceAt
  const items: TechnicalItem[] = [
    { label: tx("İş kimliği"), value: job.id },
    { label: tx("Zamanlama"), value: schedule && !schedule.enabled ? tx("{0} (kapalı)", { "0": label }) : label },
    ...(schedule ? [{ label: tx("Cron ifadesi"), value: schedule.cronExpression }, { label: tx("Saat dilimi"), value: schedule.timeZone }] : []),
    ...(next ? [{ label: tx("Sonraki çalışma"), value: formatDateTime(next) }] : []),
    { label: tx("Önceki çalışma sürerken"), value: job.overlapPolicy === 'Skip' ? tx('Atla (Skip)') : tx('Kuyruğa al (Queue)') },
    { label: tx("Şifreleme"), value: policyLabels[job.configuration.encryptionPolicy ?? 'Off'] },
  ]
  return <div role="region" aria-label={tx("İş ayrıntıları")}><TechnicalDetails items={items} /></div>
}
