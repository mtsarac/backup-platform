import { tx, useLanguage } from '@/lib/i18n'
import { Link } from 'react-router-dom'
import { Button } from '@/components/ui/button'
import { Notice } from '@/components/ui/notice'
import { Section } from '@/components/ui/section'
import { Disclosure } from '@/components/ui/disclosure'
import { QueryState } from '@/components/data/query-state'
import { useTenant } from '@/features/tenancy/use-tenant'
import { JobSummary } from '@/features/backup-jobs/job-summary'
import type { useJobOccurrences } from '@/features/backup-jobs/hooks'
import { changedSections, stepLabel, type EditSection } from '@/features/backup-jobs/job-wizard-model'
import type { JobFormState } from '@/features/backup-jobs/use-job-form'
import { failureMessage } from '@/lib/api/failure-messages'
import { formatDateTime } from '@/lib/format'
import { statusLabel } from '@/lib/status'

/** Review group: one separate job is created per selected database. */
function CreatedJobsGroup({ name, selected, results }: { name: string; selected: string[]; results: Record<string, string> }) {
  useLanguage()
  const failedCount = Object.values(results).filter(value => value !== 'ok').length
  return <Section title={tx("Oluşturulacak işler")} description={tx("{0} veritabanı için ayrı Backup Job oluşturulur. Hiçbiri 'Kaydet' düğmesine basılana kadar oluşturulmaz.", { "0": selected.length })}>
    <ul className="space-y-1 text-sm">{selected.map(database => <li key={database}>{name} – {database}{results[database] === 'ok' && <span className="text-success">{' '}{tx("· Oluşturuldu")}</span>}{results[database] && results[database] !== 'ok' && <span role="alert" className="text-error">{' '}{tx("· Oluşturulamadı:")}{' '}{results[database]}</span>}</li>)}</ul>
    {failedCount > 0 && <p className="mt-3 text-sm text-text-muted">{tx("Başarısız olanlar yeniden denenebilir; oluşturulanlar tekrar gönderilmez.")}</p>}
  </Section>
}

/** Recent schedule occurrences of an existing job. */
export function OccurrencesGroup({ occurrences }: { occurrences: ReturnType<typeof useJobOccurrences> }) {
  useLanguage()
  const { tenantId } = useTenant()
  return <Disclosure title={tx("Son zamanlama oluşumları")}><QueryState isPending={occurrences.isPending} error={occurrences.error} empty={occurrences.data?.length === 0}><ul className="space-y-2 text-sm">{occurrences.data?.map(item => <li key={item.id}>{formatDateTime(item.scheduledAt)} · {statusLabel('occurrence', item.outcome)} · {item.outcome === 'AdmissionDenied' ? failureMessage(item.reason) : statusLabel('occurrenceReason', item.reason)}{item.runId && <> · <Link className="text-primary underline" to={`/tenants/${tenantId}/backup-runs/${item.runId}`}>{tx("Yedekleme denemesini aç")}</Link></>}</li>)}</ul></QueryState></Disclosure>
}

/** The saved storage target cannot be used; the wizard can jump back to its step, the single-page form shows the section already. */
export function StorageBlockedNotice({ onFix }: { onFix?: () => void }) {
  useLanguage()
  return <Notice tone="warning" title={tx("Depolama hedefi kaydedilemiyor")} action={onFix && <Button type="button" variant="outline" size="sm" aria-label={tx("Adıma git: Depolama (hedef sorunu)")} onClick={onFix}>{tx("Adıma git")}</Button>}>{onFix ? tx("Hedef depolama bağlantısı doğrulanamadı veya kullanılamıyor. Depolama adımında hedefi düzeltin.") : tx("Hedef depolama bağlantısı doğrulanamadı veya kullanılamıyor. Depolama bölümünde hedefi düzeltin.")}</Notice>
}

/** Last wizard step: everything that will be created. */
export function ReviewStep({ form, onGo }: { form: JobFormState; onGo: (step: EditSection) => void }) {
  const { values, agent } = form
  const agentLabel = agent ? `${agent.hostName ?? tx('Adsız bilgisayar')} · ${statusLabel('agent', agent.operationalStatus)}` : values.agentId ? tx('Bilgisayar bulunamadı') : tx('Seçilmedi')
  return <>
    <JobSummary values={values} agentLabel={agentLabel} canAdmin={form.canAdmin} connections={form.picker.connections} onGo={onGo} />
    {values.selected.length > 1 && <CreatedJobsGroup name={values.name} selected={values.selected} results={form.results} />}
    {form.cannotSave && <StorageBlockedNotice onFix={() => onGo('storage')} />}
  </>
}

/** Edit page: which sections the save will change. The values themselves are already on the page, so they are not repeated. */
export function ChangesSummary({ form }: { form: JobFormState }) {
  useLanguage()
  const changed = changedSections(form.saved, form.values)
  return <Section title={tx("Kaydedilecek değişiklikler")}>
    {changed.length === 0
      ? <p className="text-sm text-text-muted">{tx("Henüz değişiklik yok.")}</p>
      : <>
        <p className="text-sm text-text">{tx("Değişen bölümler:")}{' '}{changed.map(step => stepLabel(step)).join(', ')}</p>
        <p className="mt-1 text-sm text-text-muted">{tx("Değişiklikler sonraki çalıştırmalardan itibaren geçerli olur; mevcut yedekler ve geçmiş çalıştırmalar değişmez.")}</p>
      </>}
  </Section>
}
