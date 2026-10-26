import { tx, useLanguage } from '@/lib/i18n'
import { Link } from 'react-router-dom'
import { DataList, type Column } from '@/components/data/data-list'
import { StatusPill } from '@/components/data/status-pill'
import { useTenant } from '@/features/tenancy/use-tenant'
import { linkClass } from '@/components/ui/form-styles'
import { databaseAvailability, databaseSelectable } from './status'
import type { DiscoveryDatabase, DiscoveryInstanceDetail } from './types'

/** Databases of one instance: name, whether it can be backed up and why not. Pass `onChange` to make the backupable ones pickable; without it the list is read-only. */
export function DatabaseTable({ instance, selected = [], onChange }: { instance: DiscoveryInstanceDetail; selected?: readonly string[]; onChange?: (names: string[]) => void }) {
  useLanguage()
  const { tenantId } = useTenant()
  const toggle = (name: string, checked: boolean) => onChange?.(checked ? [...selected, name] : selected.filter(item => item !== name))
  const columns: Column<DiscoveryDatabase>[] = [
    { key: 'name', card: 'title', header: tx('Veritabanı'), wrap: true, render: db => onChange
      ? <label className="flex min-h-11 items-center gap-3"><input type="checkbox" className="size-5 shrink-0 accent-primary" aria-label={tx("{0} seç", { "0": db.name })} checked={selected.includes(db.name)} disabled={!databaseSelectable(db, instance)} onChange={event => toggle(db.name, event.target.checked)} />{db.name}</label>
      : db.name },
    { key: 'backup', card: 'status', header: tx('Yedeklenebilir mi?'), render: db => {
      const { kind } = databaseAvailability(db, instance)
      if (kind === 'available') return <StatusPill level="safe">{tx("Yedeklenebilir")}</StatusPill>
      if (kind === 'covered') return <StatusPill level="info">{tx("Zaten yedekleniyor")}</StatusPill>
      return <StatusPill level={db.isSystem ? 'neutral' : 'attention'}>{tx("Yedeklenemez")}</StatusPill>
    } },
    { key: 'why', header: tx('Neden'), wrap: true, render: db => databaseAvailability(db, instance).reason ?? <span className="text-text-muted">—</span> },
    { key: 'jobs', header: tx('Yedekleme işi'), wrap: true, render: db => db.jobs.length === 0 ? <span className="text-text-muted">—</span> : <ul className="space-y-0.5">{db.jobs.map(job => <li key={job.id}><Link className={linkClass} to={`/tenants/${tenantId}/backup-jobs/${encodeURIComponent(job.id)}/edit`}>{job.name}</Link></li>)}</ul> },
  ]
  return <DataList rows={instance.databases} columns={columns} keyExtractor={db => db.name} caption={tx("Veritabanları")} emptyMessage={tx("Bu sunucuda listelenmiş veritabanı yok.")} />
}
