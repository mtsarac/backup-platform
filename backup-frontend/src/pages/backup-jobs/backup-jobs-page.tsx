import type { ReactNode } from 'react'
import { Link, useLocation } from 'react-router-dom'
import { DatabaseBackup } from 'lucide-react'
import { DataList, type Column } from '@/components/data/data-list'
import { QueryState } from '@/components/data/query-state'
import { RelativeTime } from '@/components/data/relative-time'
import { StatusPill } from '@/components/data/status-pill'
import { buttonVariants } from '@/components/ui/button-variants'
import { CheckField } from '@/components/ui/check-field'
import { FilterField } from '@/components/ui/filters'
import { linkClass } from '@/components/ui/form-styles'
import { Select } from '@/components/ui/text-input'
import { jobStatusLabel, jobStatusLevel, type JobStatus } from '@/features/backup-jobs/job-status'
import { engineLabels } from '@/features/discovery/status'
import { compareDate, compareText } from '@/features/lists/compare'
import { ListEmpty } from '@/features/lists/list-empty'
import { ListPager } from '@/features/lists/list-pager'
import { ListToolbar } from '@/features/lists/list-toolbar'
import { useClientList } from '@/features/lists/use-client-list'
import { sortProps, useListState } from '@/features/lists/use-list-state'
import type { ProtectionRow } from '@/features/dashboard/protection'
import { useTenant } from '@/features/tenancy/use-tenant'
import { currentLocale, tx, useLanguage } from '@/lib/i18n'
import { useJobRows } from '@/pages/dashboard/use-protection'

const statusFilters = { current: ['current'], attention: ['attention'], first: ['first'], running: ['running'], manual: ['manual'], inactive: ['paused'] } as const
const statusFilterKeys = Object.keys(statusFilters) as (keyof typeof statusFilters)[]
/** Columns start in the direction that fits them: names and the next backup upwards, the newest dates on top. */
const listConfig = {
  search: true,
  filters: { status: statusFilterKeys, agent: 'text', engine: 'text', revoked: ['true'] },
  sort: { first: { name: 'asc', agent: 'asc', last: 'desc', next: 'asc', created: 'desc' }, default: { key: 'created', dir: 'desc' } },
} as const

/** Only an active schedule has a next backup. */
const nextAt = ({ job }: ProtectionRow) => job.enabled && !job.agentRevoked && job.schedule?.enabled !== false ? job.nextOccurrenceAt ?? job.schedule?.nextOccurrenceAt : null
/** Jobs of revoked computers stay at the end in either direction. */
const order = (by: (a: ProtectionRow, b: ProtectionRow) => number) => (a: ProtectionRow, b: ProtectionRow) =>
  a.job.agentRevoked !== b.job.agentRevoked ? (a.job.agentRevoked ? Infinity : -Infinity)
    : by(a, b)
const comparators = {
  name: order((a, b) => compareText(a.job.name, b.job.name)),
  agent: order((a, b) => compareText(a.job.agentName, b.job.agentName)),
  last: order((a, b) => compareDate(a.lastSuccessAt, b.lastSuccessAt)),
  next: order((a, b) => compareDate(nextAt(a), nextAt(b))),
  created: order((a, b) => compareDate(a.job.createdAt, b.job.createdAt)),
}

export function BackupJobsPage() {
  useLanguage()
  const { tenantId } = useTenant()
  const { search: urlSearch } = useLocation()
  const state = useListState(listConfig)
  const data = useJobRows(tenantId)
  const base = `/tenants/${tenantId}`
  const { status, revoked } = state.filters

  const all = data.rows
  const hasRevoked = all.some(row => row.job.agentRevoked)
  const scope = revoked ? all : all.filter(row => !row.job.agentRevoked)
  const computers = [...new Map(scope.map(row => [row.job.agentId, row.job.agentName ?? tx('Adsız bilgisayar')])).entries()].toSorted((a, b) => a[1].localeCompare(b[1], currentLocale()))
  const engines = [...new Set(scope.map(row => row.job.configuration.engine))]
  // A value the list no longer offers (a computer that is gone, a hand-edited address) filters nothing.
  const agentId = computers.find(([id]) => id === state.filters.agent && computers.length > 1)?.[0]
  const engine = engines.find(item => item === state.filters.engine)
  const list = useClientList(scope, state, {
    searchText: row => `${row.job.name} ${row.job.agentName ?? ''} ${row.job.configuration.database}`,
    filters: {
      status: row => (statusFilters[status as keyof typeof statusFilters] as readonly JobStatus[]).includes(row.status),
      agent: row => !agentId || row.job.agentId === agentId,
      engine: row => !engine || row.job.configuration.engine === engine,
    },
    comparators, tieBreak: (a, b) => compareText(a.job.name, b.job.name) || compareText(a.job.id, b.job.id), pageSize: state.pageSize,
  })

  const muted = (row: ProtectionRow, node: ReactNode) => row.job.agentRevoked ? <span className="text-text-muted">{node}</span> : node
  const columns: Column<ProtectionRow>[] = [
    { key: 'name', header: tx('Ad'), sortable: true, card: 'title', wrap: true, render: row => <Link className={`font-medium ${linkClass}`} state={{ listSearch: urlSearch }} to={`${base}/backup-jobs/${encodeURIComponent(row.job.id)}`}>{row.job.name}</Link> },
    { key: 'status', header: tx('Durum'), card: 'status', render: row => <StatusPill level={jobStatusLevel[row.status]}>{jobStatusLabel(row.status, row.percent)}</StatusPill> },
    { key: 'agent', header: tx('Bilgisayar'), sortable: true, render: row => muted(row, row.job.agentName ?? tx('Adsız bilgisayar')) },
    { key: 'database', header: tx('Veritabanı'), render: row => muted(row, row.job.configuration.database || '—') },
    { key: 'last', header: tx('Son yedek'), sortable: true, render: row => muted(row, <RelativeTime value={row.lastSuccessAt} />) },
    { key: 'next', header: tx('Sonraki yedek'), sortable: true, render: row => muted(row, <RelativeTime value={nextAt(row)} />) },
    { key: 'created', header: tx('Oluşturulma'), sortable: true, render: row => muted(row, <RelativeTime value={row.job.createdAt} />) },
  ]
  const create = <Link className={buttonVariants()} to={`${base}/backup-jobs/new`}>{tx('Yedekleme işi oluştur')}</Link>
  return <QueryState isPending={data.isPending} error={data.error}>
    {all.length === 0 ? <ListEmpty icon={DatabaseBackup} message={tx('Henüz yedekleme işi oluşturulmadı.')} action={create} />
      : <div className="space-y-4">
        <ListToolbar state={state} label={tx('İş filtreleri')} total={list.total} search={{ label: tx('İş, yedekleme bilgisayarı veya veritabanı ara'), placeholder: tx('İş, bilgisayar veya veritabanı') }}
          footer={hasRevoked && <CheckField className="w-fit" label={tx('İptal edilmiş bilgisayarların işlerini göster')} checked={Boolean(revoked)} onChange={event => state.setFilter('revoked', event.target.checked ? 'true' : '')} />}>
          <FilterField label={tx('Durum')}>
            <Select aria-label={tx('Durum')} value={status} onChange={event => state.setFilter('status', event.target.value)}>
              <option value="">{tx('Tümü')}</option>{statusFilterKeys.map(key => <option key={key} value={key}>{key === 'inactive' ? tx('Pasif') : jobStatusLabel(statusFilters[key][0])}</option>)}
            </Select>
          </FilterField>
          {computers.length > 1 && <FilterField label={tx('Bilgisayar')}>
            <Select aria-label={tx('Bilgisayar')} value={agentId ?? ''} onChange={event => state.setFilter('agent', event.target.value)}>
              <option value="">{tx('Tümü')}</option>{computers.map(([id, name]) => <option key={id} value={id}>{name}</option>)}
            </Select>
          </FilterField>}
          {engines.length > 1 && <FilterField label={tx('Veritabanı türü')}>
            <Select aria-label={tx('Veritabanı türü')} value={engine ?? ''} onChange={event => state.setFilter('engine', event.target.value)}>
              <option value="">{tx('Tümü')}</option>{engines.map(item => <option key={item} value={item}>{engineLabels[item] ?? item}</option>)}
            </Select>
          </FilterField>}
        </ListToolbar>
        <DataList rows={list.rows} columns={columns} keyExtractor={row => row.job.id} caption={tx('Yedekleme işleri')} sort={sortProps(state)}
          emptyMessage={state.filtering ? tx('Aramanızla eşleşen iş yok.') : tx('Tüm işler iptal edilmiş bilgisayarlara ait.')} onClearFilters={state.filtering ? state.clear : undefined} />
        <ListPager state={state} total={list.total} label={tx('Yedekleme işi sayfaları')} />
      </div>}
  </QueryState>
}
