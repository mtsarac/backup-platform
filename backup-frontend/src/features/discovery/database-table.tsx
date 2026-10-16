import { tx, useLanguage } from '@/lib/i18n'
import { Link } from 'react-router-dom'
import { DataTable, type Column } from '@/components/data/data-table'
import { useTenant } from '@/features/tenancy/use-tenant'
import { DatabaseStatePill, FullBackupPill, PresencePill } from './presentation'
import { databaseBlockedReason, databaseSelectable, reasonText } from './status'
import type { DiscoveryDatabase, DiscoveryInstanceDetail } from './types'

/** Databases of one instance. Pass `onChange` to make the selectable ones pickable; without it the table is read-only. */
export function DatabaseTable({ instance, selected = [], onChange }: { instance: DiscoveryInstanceDetail; selected?: readonly string[]; onChange?: (names: string[]) => void }) {
  useLanguage()
  const { tenantId } = useTenant()
  const toggle = (name: string, checked: boolean) => onChange?.(checked ? [...selected, name] : selected.filter(item => item !== name))
  const columns: Column<DiscoveryDatabase>[] = [
    ...(onChange ? [{ key: 'pick', header: tx('Seç'), render: (db: DiscoveryDatabase) => <input type="checkbox" className="size-5 accent-primary" aria-label={tx("{0} seç", { "0": db.name })} checked={selected.includes(db.name)} disabled={!databaseSelectable(db, instance)} onChange={event => toggle(db.name, event.target.checked)} /> }] : []),
    { key: 'name', header: tx('Database'), wrap: true, render: db => db.name },
    { key: 'kind', header: tx('Tür'), render: db => db.isSystem ? tx('Sistem') : tx('Kullanıcı') },
    { key: 'state', header: tx('Durum'), render: db => <DatabaseStatePill value={db.state} /> },
    { key: 'backup', header: tx('Full Backup'), wrap: true, render: db => <div className="space-y-1"><FullBackupPill value={db.fullBackup} />{db.fullBackup === 'Unsupported' && reasonText(db.fullBackupReasonCode) && <p className="text-xs text-text-muted">{reasonText(db.fullBackupReasonCode)}</p>}</div> },
    { key: 'presence', header: tx('Varlık'), render: db => <PresencePill value={db.presence} /> },
    { key: 'jobs', header: tx('Mevcut işler'), wrap: true, render: db => db.jobs.length === 0 ? <span className="text-text-muted">—</span> : <ul className="space-y-0.5">{db.jobs.map(job => <li key={job.id}><Link className="text-primary underline-offset-4 hover:underline" to={`/tenants/${tenantId}/backup-jobs/${encodeURIComponent(job.id)}/edit`}>{job.name}</Link></li>)}</ul> },
    ...(onChange ? [{ key: 'why', header: tx('Seçilebilirlik'), wrap: true, render: (db: DiscoveryDatabase) => databaseBlockedReason(db, instance) ?? <span className="text-success">{tx("Seçilebilir")}</span> }] : []),
  ]
  return <DataTable rows={instance.databases} columns={columns} keyExtractor={db => db.name} emptyMessage={tx("Listelenmiş veritabanı yok.")} />
}
