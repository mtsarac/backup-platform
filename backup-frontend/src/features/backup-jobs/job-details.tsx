import { tx, useLanguage } from '@/lib/i18n'
import { TechnicalDetails, type TechnicalItem } from '@/components/ui/technical-details'
import { CopyButton } from '@/components/ui/copy-button'
import { Disclosure } from '@/components/ui/disclosure'
import { policyLabels } from '@/features/artifact-encryption/labels'
import { schedulePresetLabel } from '@/features/backup-jobs/job-wizard-model'
import type { BackupJob } from '@/features/backup-jobs/types'
import { formatDateTime } from '@/lib/format'

// Viewers need schedule and encryption details because they have no edit form
export function JobDetails({ job, readOnly }: { job: BackupJob; readOnly: boolean }) {
  useLanguage()
  if (!readOnly) return <div role="region" aria-label={tx('İş ayrıntıları')}><Disclosure title={tx('İş kimliği')}><div className="flex flex-wrap items-center gap-2 text-sm"><span className="break-all font-mono">{job.id}</span><CopyButton value={job.id} label={tx('Kopyala: {0}', { '0': tx('İş kimliği') })} /></div></Disclosure></div>
  const { schedule } = job
  const label = schedule ? schedulePresetLabel(schedule.cronExpression) ?? tx('Özel zamanlama') : tx('Manuel')
  const next = job.nextOccurrenceAt ?? schedule?.nextOccurrenceAt
  const items: TechnicalItem[] = [
    { label: tx("İş kimliği"), value: job.id },
    ...(readOnly ? [
      { label: tx("Zamanlama"), value: schedule && !schedule.enabled ? tx("{0} (kapalı)", { "0": label }) : label },
      ...(schedule ? [{ label: tx("Cron ifadesi"), value: schedule.cronExpression }, { label: tx("Saat dilimi"), value: schedule.timeZone }] : []),
      ...(next ? [{ label: tx("Sonraki çalışma"), value: formatDateTime(next) }] : []),
      { label: tx("Önceki çalışma sürerken"), value: job.overlapPolicy === 'Skip' ? tx('Atla (Skip)') : tx('Kuyruğa al (Queue)') },
      { label: tx("Şifreleme"), value: policyLabels[job.configuration.encryptionPolicy ?? 'Off'] },
    ] : []),
  ]
  return <div role="region" aria-label={tx("İş ayrıntıları")}><TechnicalDetails items={items} /></div>
}
