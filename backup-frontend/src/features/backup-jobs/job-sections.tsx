import { tx, useLanguage } from '@/lib/i18n'
import { useState, type FormEvent } from 'react'
import { Button } from '@/components/ui/button'
import { ErrorNotice } from '@/components/ui/notice'
import { Fact, Facts, Section } from '@/components/ui/section'
import { DiscardDialog, UnsavedChangesGuard } from '@/features/backup-jobs/discard-changes-link'
import { fieldKit } from '@/features/backup-jobs/job-field-kit'
import { FormGrid } from '@/features/backup-jobs/job-form-fields'
import { FilePathList } from '@/features/backup-jobs/file-path-list'
import { defaultFiles, hasNonStandardSettings, initialValues } from '@/features/backup-jobs/job-form-values'
import { summaryInput, summaryRows } from '@/features/backup-jobs/job-summary-rows'
import { editSections, validateEdit, type EditSection } from '@/features/backup-jobs/job-wizard-model'
import { ProtectionStep } from '@/features/backup-jobs/steps/protection-step'
import { RetentionQuestion, ScheduleQuestion } from '@/features/backup-jobs/steps/schedule-step'
import { StorageStep } from '@/features/backup-jobs/steps/storage-step'
import { StorageBlockedNotice } from '@/features/backup-jobs/steps/summary-step'
import type { BackupJob } from '@/features/backup-jobs/types'
import { useJobForm, type JobFormState } from '@/features/backup-jobs/use-job-form'

function sectionTitle(section: EditSection) {
  switch (section) {
    case 'source': return tx('Kaynak')
    case 'storage': return tx('Hedef')
    case 'schedule': return tx('Zamanlama')
    case 'protection': return tx('Saklama ve şifreleme')
  }
}

/** Name and file paths are editable; the computer, data type and database stay as they were created, so the job's history keeps its meaning. */
function SourceFields({ form }: { form: JobFormState }) {
  useLanguage()
  const { values, errors, set, setConfiguration, agent, job } = form
  const { configuration } = values
  const { text } = fieldKit(errors)
  const files = configuration.files ?? defaultFiles
  const pathError = Object.entries(errors).find(([key]) => key.startsWith('configuration.files'))?.[1]
  return <div className="space-y-4">
    <FormGrid>{text('name', tx('İş adı'), values.name, name => set({ name }))}</FormGrid>
    {configuration.engine === 'file-system' && <FilePathList id="configuration.files.paths" label={tx('Yedeklenecek dosya ve klasörler')} paths={files.paths} error={pathError} onChange={paths => setConfiguration({ ...configuration, files: { ...files, paths } })} />}
    <Facts>
      <Fact label={tx('Yedekleme bilgisayarı')}>{agent?.hostName ?? job?.agentName ?? tx('Adsız bilgisayar')}</Fact>
      <Fact label={tx('Veri türü')}>{configuration.engine === 'file-system' ? tx('Dosya / Klasör') : configuration.engine === 'mssql' ? 'Microsoft SQL Server' : 'PostgreSQL'}</Fact>
      <Fact label={tx('Veritabanı')} hint={tx('Kurulduktan sonra değiştirilemez; farklı bir veri için yeni bir korunan veri oluşturun.')}>{configuration.database}</Fact>
    </Facts>
  </div>
}

/** A job's Kaynak, Hedef, Zamanlama and Saklama sections: each reads as plain rows and turns into its own form with Kaydet and Vazgeç. */
export function JobSections({ job }: { job: BackupJob }) {
  useLanguage()
  const [editing, setEditing] = useState<EditSection | null>(null)
  const [asked, setAsked] = useState<EditSection | null>(null)
  const form = useJobForm({ job, onSaved: () => setEditing(null) })
  const input = summaryInput(form)
  const rows = summaryRows({ ...input, agentLabel: job.agentRevoked ? `${job.agentName ?? tx('Adsız bilgisayar')} · ${tx('Bilgisayar iptal edildi')}` : input.agentLabel, values: initialValues(job) })
  const dirty = editing !== null && form.dirty
  function begin(section: EditSection) {
    form.restart(job)
    setAsked(null)
    setEditing(section)
  }
  function stop() {
    form.restart()
    setEditing(null)
  }
  function submit(event: FormEvent) {
    event.preventDefault()
    if (editing === null) return
    const found = validateEdit(editing, form.values)
    form.setClientErrors(found)
    if (Object.keys(found).length === 0) form.submit()
  }
  return <div className="space-y-5">
    {editSections.map(section => {
      const title = sectionTitle(section)
      const open = editing === section
      return <Section key={section} headingLevel={3} title={title} actions={!open && <Button type="button" variant="outline" size="sm" aria-label={tx('Değiştir: {0}', { '0': title })} onClick={() => dirty ? setAsked(section) : begin(section)}>{tx('Değiştir')}</Button>}>
        {open ? <form noValidate aria-label={title} className="space-y-4" onSubmit={submit}>
          {section === 'source' && <SourceFields form={form} />}
          {section === 'storage' && <StorageStep form={form} />}
          {section === 'schedule' && <ScheduleQuestion form={form} headingLevel={3} />}
          {section === 'protection' && <><RetentionQuestion form={form} headingLevel={3} /><ProtectionStep form={form} headingLevel={3} /></>}
          {form.cannotSave && section === 'storage' && <StorageBlockedNotice />}
          {form.saveError && <ErrorNotice title={tx('İş kaydedilemedi')} error={form.saveError} />}
          <div className="flex flex-wrap items-center gap-2">
            <Button type="submit" disabled={form.save.isPending || form.cannotSave}>{form.save.isPending ? tx('Kaydediliyor…') : tx('Kaydet')}</Button>
            <Button type="button" variant="outline" disabled={form.save.isPending} onClick={stop}>{tx('Vazgeç')}</Button>
          </div>
          {hasNonStandardSettings(job) && <p className="text-sm text-text-muted">{tx('Bu işte standart dışı gelişmiş ayarlar var; korunuyor.')}</p>}
        </form>
          : <dl className="space-y-3">{rows.filter(row => row.step === section).map(row => <div key={row.label}><dt className="text-sm text-text-muted">{row.label}</dt><dd className="mt-0.5 break-words text-text">{row.value}</dd></div>)}</dl>}
      </Section>
    })}
    <UnsavedChangesGuard dirty={dirty} />
    <DiscardDialog open={asked !== null} title={tx('Değişiklikler kaydedilmedi')} onClose={() => setAsked(null)} onDiscard={() => asked && begin(asked)} />
  </div>
}
