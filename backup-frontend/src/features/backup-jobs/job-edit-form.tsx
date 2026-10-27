import { tx, useLanguage } from '@/lib/i18n'
import { useEffect } from 'react'
import { Button } from '@/components/ui/button'
import { ErrorNotice } from '@/components/ui/notice'
import { DiscardChangesLink } from '@/features/backup-jobs/discard-changes-link'
import { useJobOccurrences } from '@/features/backup-jobs/hooks'
import { validateJob } from '@/features/backup-jobs/job-wizard-model'
import { SourceStep } from '@/features/backup-jobs/steps/source-step'
import { StorageStep } from '@/features/backup-jobs/steps/storage-step'
import { ScheduleStep } from '@/features/backup-jobs/steps/schedule-step'
import { ProtectionStep } from '@/features/backup-jobs/steps/protection-step'
import { ChangesSummary, OccurrencesGroup, StorageBlockedNotice } from '@/features/backup-jobs/steps/summary-step'
import { useJobForm } from '@/features/backup-jobs/use-job-form'
import type { BackupJob } from '@/features/backup-jobs/types'

/** One page with every setting of an existing job: no steps, one Kaydet, and a list of what will change. */
export function JobEditForm({ job }: { job: BackupJob }) {
  useLanguage()
  const form = useJobForm({ job })
  const occurrences = useJobOccurrences(job.id)
  const { saveError, save, setClientErrors } = form

  useEffect(() => {
    if (saveError) document.querySelector<HTMLElement>('form [aria-invalid="true"]')?.focus()
  }, [saveError])

  return <form noValidate className="space-y-5" onSubmit={event => {
    event.preventDefault()
    const { errors } = validateJob(form.values)
    setClientErrors(errors)
    if (Object.keys(errors).length === 0) form.submit()
  }}>
    <SourceStep form={form} />
    <StorageStep form={form} />
    <ScheduleStep form={form} />
    <ProtectionStep form={form} />
    <OccurrencesGroup occurrences={occurrences} />
    <ChangesSummary form={form} />
    {form.cannotSave && <StorageBlockedNotice />}
    {saveError && <ErrorNotice title={tx("İş kaydedilemedi")} error={saveError} />}
    <div className="flex flex-wrap items-center justify-end gap-3">
      <DiscardChangesLink to={form.listPath} dirty={form.dirty} />
      <Button type="submit" size="lg" disabled={save.isPending || form.cannotSave}>{save.isPending ? tx('Kaydediliyor…') : tx('Kaydet')}</Button>
    </div>
  </form>
}
