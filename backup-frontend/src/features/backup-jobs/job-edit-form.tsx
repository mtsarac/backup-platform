import { tx, useLanguage } from '@/lib/i18n'
import { useLayoutEffect, useRef, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Tabs } from '@/components/ui/tabs'
import { ErrorNotice } from '@/components/ui/notice'
import { DiscardChangesLink } from '@/features/backup-jobs/discard-changes-link'
import { useJobOccurrences } from '@/features/backup-jobs/hooks'
import { editSections, stepLabel, validateJob, type EditSection } from '@/features/backup-jobs/job-wizard-model'
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
  const formRef = useRef<HTMLFormElement>(null)
  const [active, setActive] = useState<EditSection>('source')
  const { saveError, save, setClientErrors } = form

  useLayoutEffect(() => {
    const field = formRef.current?.querySelector<HTMLElement>('[aria-invalid="true"]')
    if (!field) {
      if (saveError && form.errorSteps[0]) setActive(form.errorSteps[0])
      return
    }
    for (let parent = field.parentElement; parent && parent !== formRef.current; parent = parent.parentElement) {
      if (parent instanceof HTMLDetailsElement) parent.open = true
    }
    const panel = field.closest<HTMLElement>('[role="tabpanel"]')
    const section = editSections.find(item => panel?.id === `job-edit-panel-${item}`)
    if (section) setActive(section)
    const frame = requestAnimationFrame(() => field.focus())
    return () => cancelAnimationFrame(frame)
  }, [form.clientErrors, saveError])

  return <form ref={formRef} noValidate className="space-y-5" onSubmit={event => {
    event.preventDefault()
    const { errors } = validateJob(form.values)
    setClientErrors(errors)
    if (Object.keys(errors).length === 0) form.submit()
  }}>
    <Tabs label={tx('İş ayarları')} value={active} onChange={setActive} items={editSections.map(value => ({ value, label: stepLabel(value) }))} idPrefix="job-edit" />
    <div role="tabpanel" id="job-edit-panel-source" aria-labelledby="job-edit-tab-source" hidden={active !== 'source'}><SourceStep form={form} /></div>
    <div role="tabpanel" id="job-edit-panel-storage" aria-labelledby="job-edit-tab-storage" hidden={active !== 'storage'}><StorageStep form={form} /></div>
    <div role="tabpanel" id="job-edit-panel-schedule" aria-labelledby="job-edit-tab-schedule" hidden={active !== 'schedule'}><ScheduleStep form={form} /><OccurrencesGroup occurrences={occurrences} /></div>
    <div role="tabpanel" id="job-edit-panel-protection" aria-labelledby="job-edit-tab-protection" hidden={active !== 'protection'}><ProtectionStep form={form} /></div>
    <ChangesSummary form={form} />
    {form.cannotSave && <StorageBlockedNotice onFix={() => setActive('storage')} />}
    {saveError && <ErrorNotice title={tx("İş kaydedilemedi")} error={saveError} />}
    <div className="flex flex-wrap items-center justify-end gap-3">
      <DiscardChangesLink to={form.listPath} dirty={form.dirty} />
      <Button type="submit" size="lg" disabled={save.isPending || form.cannotSave}>{save.isPending ? tx('Kaydediliyor…') : tx('Kaydet')}</Button>
    </div>
  </form>
}
