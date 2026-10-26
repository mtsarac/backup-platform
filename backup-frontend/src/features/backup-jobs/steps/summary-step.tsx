import { tx, useLanguage } from '@/lib/i18n'
import { Link } from 'react-router-dom'
import { Button } from '@/components/ui/button'
import { Notice } from '@/components/ui/notice'
import { Section } from '@/components/ui/section'
import { QueryState } from '@/components/data/query-state'
import { useTenant } from '@/features/tenancy/use-tenant'
import { JobSummary, type JobSummaryProps } from '@/features/backup-jobs/job-summary'
import type { useJobOccurrences } from '@/features/backup-jobs/hooks'
import { failureMessage } from '@/lib/api/failure-messages'
import { formatDateTime } from '@/lib/format'

/** Review group: one separate job is created per selected database. */
export function CreatedJobsGroup({ name, selected, results }: { name: string; selected: string[]; results: Record<string, string> }) {
  useLanguage()
  const failedCount = Object.values(results).filter(value => value !== 'ok').length
  return <Section title={tx("Oluşturulacak işler")} description={tx("{0} veritabanı için ayrı Backup Job oluşturulur. Hiçbiri 'Kaydet' düğmesine basılana kadar oluşturulmaz.", { "0": selected.length })}>
    <ul className="space-y-1 text-sm">{selected.map(database => <li key={database}>{name} – {database}{results[database] === 'ok' && <span className="text-success">{' '}{tx("· Oluşturuldu")}</span>}{results[database] && results[database] !== 'ok' && <span role="alert" className="text-error">{' '}{tx("· Oluşturulamadı:")}{' '}{results[database]}</span>}</li>)}</ul>
    {failedCount > 0 && <p className="mt-3 text-sm text-text-muted">{tx("Başarısız olanlar yeniden denenebilir; oluşturulanlar tekrar gönderilmez.")}</p>}
  </Section>
}

/** Review group: recent schedule occurrences of an existing job. */
export function OccurrencesGroup({ occurrences }: { occurrences: ReturnType<typeof useJobOccurrences> }) {
  useLanguage()
  const { tenantId } = useTenant()
  return <Section title={tx("Son zamanlama oluşumları")}><QueryState isPending={occurrences.isPending} error={occurrences.error} empty={occurrences.data?.length === 0}><ul className="space-y-2 text-sm">{occurrences.data?.map(item => <li key={item.id}>{formatDateTime(item.scheduledAt)} · {tx(item.outcome)} · {item.outcome === 'AdmissionDenied' ? failureMessage(item.reason) : item.reason}{item.runId && <> · <Link className="text-primary underline" to={`/tenants/${tenantId}/backup-runs/${item.runId}`}>{tx("Çalıştırmayı aç")}</Link></>}</li>)}</ul></QueryState></Section>
}

/** Review group: the saved storage target cannot be used. */
export function StorageBlockedNotice({ onFix }: { onFix: () => void }) {
  useLanguage()
  return <Notice tone="warning" title={tx("Depolama hedefi kaydedilemiyor")} action={<Button type="button" variant="outline" size="sm" aria-label={tx("Adıma git: Depolama (hedef sorunu)")} onClick={onFix}>{tx("Adıma git")}</Button>}>{tx("Hedef depolama bağlantısı doğrulanamadı veya kullanılamıyor. Depolama adımında hedefi düzeltin.")}</Notice>
}

interface Props { summary: JobSummaryProps; selected: string[]; results: Record<string, string>; occurrences?: ReturnType<typeof useJobOccurrences>; cannotSave: boolean; onFix: () => void }

export function SummaryStep({ summary, selected, results, occurrences, cannotSave, onFix }: Props) {
  return <>
    <JobSummary {...summary} />
    {selected.length > 1 && <CreatedJobsGroup name={summary.name} selected={selected} results={results} />}
    {occurrences && <OccurrencesGroup occurrences={occurrences} />}
    {cannotSave && <StorageBlockedNotice onFix={onFix} />}
  </>
}
