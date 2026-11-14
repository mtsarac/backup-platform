import { tx, useLanguage } from '@/lib/i18n'
import { useEffect, useRef, useState } from 'react'
import { Button } from '@/components/ui/button'
import { ErrorNotice } from '@/components/ui/notice'
import { Stepper } from '@/components/ui/stepper'
import { DiscardChangesLink } from '@/features/backup-jobs/discard-changes-link'
import type { JobPrefill } from '@/features/backup-jobs/job-form-values'
import { jobSteps, stepLabel, validateJob, validateStep, wizardStepOf, type JobStep } from '@/features/backup-jobs/job-wizard-model'
import { SourceStep } from '@/features/backup-jobs/steps/source-step'
import { StorageStep } from '@/features/backup-jobs/steps/storage-step'
import { ScheduleStep } from '@/features/backup-jobs/steps/schedule-step'
import { ProtectionStep } from '@/features/backup-jobs/steps/protection-step'
import { ReviewStep } from '@/features/backup-jobs/steps/summary-step'
import { useJobForm } from '@/features/backup-jobs/use-job-form'

/** Creates backup jobs one question group at a time; existing jobs are edited on a single page instead (`JobEditForm`). */
export function JobWizard({ prefill }: { prefill?: JobPrefill }) {
  useLanguage()
  const form = useJobForm({ prefill })
  const { values, save, saveError, errorSteps, setClientErrors } = form
  const [index, setIndex] = useState(0)
  const step = jobSteps[index]
  const mounted = useRef(false)
  const headingRef = useRef<HTMLHeadingElement>(null)
  const last = step === 'review'

  useEffect(() => {
    if (!mounted.current) { mounted.current = true; return }
    headingRef.current?.focus()
  }, [index])

  function go(target: JobStep) {
    setClientErrors({})
    setIndex(jobSteps.indexOf(target))
  }
  function advance() {
    const found = validateStep(step, values)
    if (Object.keys(found).length) setClientErrors(found)
    else go(jobSteps[index + 1])
  }
  function submit() {
    // Steps can be revisited out of order, so everything is checked again before anything is created.
    const { firstInvalid } = validateJob(values)
    if (firstInvalid) { go(wizardStepOf(firstInvalid)); setClientErrors(validateStep(wizardStepOf(firstInvalid), values)) }
    else form.submit()
  }
  return <form noValidate className="space-y-5" onSubmit={event => {
    event.preventDefault()
    if (last) submit()
    else advance()
  }}>
    <Stepper steps={jobSteps.map(item => stepLabel(item))} current={index} />
    <h2 ref={headingRef} tabIndex={-1} className="sr-only">{tx("Adım")}{' '}{index + 1} / {jobSteps.length}: {stepLabel(step)}</h2>
    {step === 'source' && <SourceStep form={form} />}
    {step === 'storage' && <StorageStep form={form} />}
    {step === 'schedule-protection' && <>
      <section aria-labelledby="wizard-schedule" className="space-y-5">
        <h2 id="wizard-schedule" className="text-lg font-semibold text-text-heading">{tx("Zamanlama")}</h2>
        <ScheduleStep form={form} headingLevel={3} advancedTitle={tx("Zamanlama teknik ayrıntıları")} />
      </section>
      <section aria-labelledby="wizard-protection" className="space-y-5 border-t border-border pt-5">
        <h2 id="wizard-protection" className="text-lg font-semibold text-text-heading">{tx("Koruma")}</h2>
        <ProtectionStep form={form} headingLevel={3} advancedTitle={tx("Koruma teknik ayrıntıları")} />
      </section>
    </>}
    {last && <ReviewStep form={form} onGo={section => go(wizardStepOf(section))} />}
    {saveError && <ErrorNotice title={tx("İş kaydedilemedi")} error={saveError}
      control={errorSteps.length > 0 ? <div className="flex flex-wrap gap-2">{[...new Set(errorSteps.map(wizardStepOf))].map(item => <Button key={item} type="button" variant="outline" size="sm" aria-label={tx("Hatalı alana git: {0}", { "0": stepLabel(item) })} onClick={() => go(item)}>{tx("Adıma git")}</Button>)}</div> : undefined} />}
    <div className="flex flex-wrap items-center justify-between gap-3">
      <Button type="button" variant="outline" disabled={index === 0 || save.isPending} onClick={() => go(jobSteps[index - 1])}>{tx("Geri")}</Button>
      <div className="flex flex-wrap items-center gap-3">
        <DiscardChangesLink to={form.listPath} dirty={form.dirty} />
        {last ? <Button key="save" type="submit" size="lg" disabled={save.isPending || form.cannotSave}>{save.isPending ? tx('Kaydediliyor…') : form.failedCount > 0 ? tx('Başarısızları yeniden dene') : tx('Kaydet')}</Button> : <Button key="next" type="submit">{tx("İleri")}</Button>}
      </div>
    </div>
  </form>
}
