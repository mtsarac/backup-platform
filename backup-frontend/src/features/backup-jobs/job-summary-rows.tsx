import { tx } from '@/lib/i18n'
import type { ReactNode } from 'react'
import { keepForLabel, schedulePresetLabel, type EditSection } from '@/features/backup-jobs/job-wizard-model'
import type { JobValues } from '@/features/backup-jobs/job-form-values'
import { encryptionStateLabel } from '@/features/artifact-encryption/labels'
import { destinationName } from '@/features/storage-connections/job-connection-picker-utils'
import type { ConnectionView } from '@/features/storage-connections/types'
import type { JobFormState } from '@/features/backup-jobs/use-job-form'
import { statusLabel } from '@/lib/status'

export interface SummaryInput { values: JobValues; agentLabel: string; canAdmin: boolean; connections: readonly ConnectionView[] }

/** The values a summary row shows, taken from the form; shared by the review step and the live summary. */
export function summaryInput(form: JobFormState): SummaryInput {
  const { values, agent } = form
  const agentLabel = agent ? `${agent.hostName ?? tx('Adsız bilgisayar')} · ${statusLabel('agent', agent.operationalStatus)}` : values.agentId ? tx('Bilgisayar bulunamadı') : tx('Seçilmedi')
  return { values, agentLabel, canAdmin: form.canAdmin, connections: form.picker.connections }
}

/** What the job will do, in plain words and in review order; the review step and the live summary beside the form both show these rows. */
export function summaryRows({ values, agentLabel, canAdmin, connections }: SummaryInput): { label: string; step: EditSection; value: ReactNode }[] {
  const { name, enabled, configuration: c, scheduled, schedule, retention } = values
  const retentionText = !canAdmin ? tx('Sunucunun varsayılan politikası uygulanır.') : retention.enabled ? keepForLabel(retention.keepForSeconds) : tx('Hiç silinmesin')
  return [
    { label: tx('İş adı'), step: 'source', value: enabled ? name || '—' : tx("{0} (devre dışı)", { "0": name || '—' }) },
    { label: tx('Bilgisayar'), step: 'source', value: agentLabel },
    { label: tx('Kaynak'), step: 'source', value: c.engine === 'file-system'
      ? <>{tx('Dosya / Klasör')} · {c.database}<span className="block whitespace-pre-line">{c.files?.paths.join('\n')}</span></>
      : `${c.engine === 'mssql' ? 'Microsoft SQL Server' : 'PostgreSQL'} · ${c.database || '—'} (${c.host || '—'})` },
    { label: tx('Depolama'), step: 'storage', value: <>
      <ul className="space-y-1">{c.destinations.map((destination, index) => <li key={index}>{destinationName(destination, connections)}</li>)}</ul>
      {c.destinations.length > 1 && <span className="block text-sm text-text-muted">{tx("{0} hedeften en az {1} tanesi doğrulanmalıdır.", { "0": c.destinations.length, "1": c.requiredVerifiedReplicaCount })}</span>}
    </> },
    { label: tx('Zamanlama'), step: 'schedule', value: scheduled ? schedulePresetLabel(schedule.cronExpression) ?? tx('Özel zamanlama') : tx('Elle çalıştırılır (zamanlama yok)') },
    { label: tx('Saklama'), step: 'protection', value: <>{retentionText}{!canAdmin && <span className="block text-sm text-text-muted">{tx("Yalnızca Owner / Admin değiştirebilir.")}</span>}</> },
    { label: tx('Şifreleme'), step: 'protection', value: encryptionStateLabel(c.encryptionPolicy) },
  ]
}
