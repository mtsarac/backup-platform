import type { ReactNode } from 'react'
import { Link, useLocation } from 'react-router-dom'
import { DatabaseBackup } from 'lucide-react'
import { DataList, type Column, type SortDirection } from '@/components/data/data-list'
import { Pagination } from '@/components/data/pagination'
import { QueryState } from '@/components/data/query-state'
import { RelativeTime } from '@/components/data/relative-time'
import { StatusPill } from '@/components/data/status-pill'
import { buttonVariants } from '@/components/ui/button-variants'
import { Card } from '@/components/ui/card'
import { CheckField } from '@/components/ui/check-field'
import { EmptyState } from '@/components/ui/empty-state'
import { FilterField, Filters } from '@/components/ui/filters'
import { linkClass } from '@/components/ui/form-styles'
import { Select, TextInput } from '@/components/ui/text-input'
import { jobStatusLabel, jobStatusLevel, type JobStatus } from '@/features/backup-jobs/job-status'
import { LIST_PAGE_SIZE, oneOf, useListParams } from '@/features/platform/use-list-params'
import type { ProtectionRow } from '@/features/dashboard/protection'
import { useTenant } from '@/features/tenancy/use-tenant'
import { currentLocale, tx, useLanguage } from '@/lib/i18n'
import { useJobRows } from '@/pages/dashboard/use-protection'

const sortKeys = ['name', 'agent', 'last', 'next', 'created'] as const
type SortKey = typeof sortKeys[number]
/** The direction a column sorts in when it is chosen first: names and the next backup upwards, the newest dates on top. */
const firstDirection: Record<SortKey, SortDirection> = { name: 'asc', agent: 'asc', last: 'desc', next: 'asc', created: 'desc' }

// The filter's "Pasif" covers both jobs that do not run on their own: stopped ones and ones without a schedule.
const statusFilters = { current: ['current'], attention: ['attention'], first: ['first'], running: ['running'], inactive: ['paused', 'manual'] } as const
type StatusFilter = keyof typeof statusFilters
const statusFilterKeys = Object.keys(statusFilters) as StatusFilter[]

const time = (value?: string | null) => value ? Date.parse(value) : null
/** Only an active schedule has a next backup. */
const nextAt = ({ job }: ProtectionRow) => job.enabled && !job.agentRevoked && job.schedule?.enabled !== false ? job.nextOccurrenceAt ?? job.schedule?.nextOccurrenceAt : null
const sortValue: Record<SortKey, (row: ProtectionRow) => string | number | null> = {
  name: row => row.job.name,
  agent: row => row.job.agentName ?? null,
  last: row => time(row.lastSuccessAt),
  next: row => time(nextAt(row)),
  created: row => time(row.job.createdAt),
}

/** A row without a value goes last in either direction. */
function compare(a: ProtectionRow, b: ProtectionRow, key: SortKey, direction: SortDirection) {
  const x = sortValue[key](a), y = sortValue[key](b)
  const byName = a.job.name.localeCompare(b.job.name, currentLocale()) || a.job.id.localeCompare(b.job.id)
  if (x === null || y === null) return x === y ? byName : x === null ? 1 : -1
  const order = typeof x === 'string' ? x.localeCompare(String(y), currentLocale()) : x - (y as number)
  return (direction === 'asc' ? order : -order) || byName
}

export function BackupJobsPage() {
  useLanguage()
  const { tenantId } = useTenant()
  const { search: urlSearch } = useLocation()
  const { page, get, update } = useListParams()
  const data = useJobRows(tenantId)
  const base = `/tenants/${tenantId}`
  const search = get('search')
  const showRevoked = get('revoked') === 'true'
  const status = oneOf(get('status'), statusFilterKeys)
  const sortKey = oneOf(get('sort'), sortKeys) ?? 'created'
  const direction = oneOf(get('dir'), ['asc', 'desc'] as const) ?? firstDirection[sortKey]

  const all = data.rows
  const hasRevoked = all.some(row => row.job.agentRevoked)
  const scope = showRevoked ? all : all.filter(row => !row.job.agentRevoked)
  const computers = [...new Map(scope.map(row => [row.job.agentId, row.job.agentName ?? tx('Adsız bilgisayar')])).entries()].toSorted((a, b) => a[1].localeCompare(b[1], currentLocale()))
  const engines = [...new Set(scope.map(row => row.job.configuration.engine))]
  // A value the list no longer offers (a computer that is gone, a hand-edited address) filters nothing.
  const agentId = computers.find(([id]) => id === get('agent') && computers.length > 1)?.[0]
  const engine = engines.find(item => item === get('engine'))
  const needle = search.trim().toLocaleLowerCase(currentLocale())
  const filtering = Boolean(needle || status || agentId || engine)
  const matching = scope.filter(row => (!status || (statusFilters[status] as readonly JobStatus[]).includes(row.status))
    && (!agentId || row.job.agentId === agentId) && (!engine || row.job.configuration.engine === engine)
    && `${row.job.name} ${row.job.agentName ?? ''} ${row.job.configuration.database}`.toLocaleLowerCase(currentLocale()).includes(needle))
  const byOrder = (a: ProtectionRow, b: ProtectionRow) => compare(a, b, sortKey, direction)
  const sorted = [...matching.filter(row => !row.job.agentRevoked).toSorted(byOrder), ...matching.filter(row => row.job.agentRevoked).toSorted(byOrder)]
  const lastPage = Math.max(1, Math.ceil(sorted.length / LIST_PAGE_SIZE))
  const current = Math.min(page, lastPage)
  const visible = sorted.slice((current - 1) * LIST_PAGE_SIZE, current * LIST_PAGE_SIZE)
  const clear = () => update({ search: '', status: '', agent: '', engine: '' })

  const muted = (row: ProtectionRow, node: ReactNode) => row.job.agentRevoked ? <span className="text-text-muted">{node}</span> : node
  const columns: Column<ProtectionRow>[] = [
    { key: 'name', header: tx('Ad'), sortable: firstDirection.name, card: 'title', wrap: true, render: row => <Link className={`font-medium ${linkClass}`} state={{ listSearch: urlSearch }} to={`${base}/backup-jobs/${encodeURIComponent(row.job.id)}`}>{row.job.name}</Link> },
    { key: 'status', header: tx('Durum'), card: 'status', render: row => <StatusPill level={jobStatusLevel[row.status]}>{jobStatusLabel(row.status, row.percent)}</StatusPill> },
    { key: 'agent', header: tx('Bilgisayar'), sortable: firstDirection.agent, render: row => muted(row, row.job.agentName ?? tx('Adsız bilgisayar')) },
    { key: 'database', header: tx('Veritabanı'), render: row => muted(row, row.job.configuration.database || '—') },
    { key: 'last', header: tx('Son yedek'), sortable: firstDirection.last, render: row => muted(row, <RelativeTime value={row.lastSuccessAt} />) },
    { key: 'next', header: tx('Sonraki yedek'), sortable: firstDirection.next, render: row => muted(row, <RelativeTime value={nextAt(row)} />) },
    { key: 'created', header: tx('Oluşturulma'), sortable: firstDirection.created, render: row => muted(row, <RelativeTime value={row.job.createdAt} />) },
  ]
  const create = <Link className={buttonVariants()} to={`${base}/backup-jobs/new`}>{tx('Yedekleme işi oluştur')}</Link>
  return <QueryState isPending={data.isPending} error={data.error}>
    {all.length === 0 ? <Card padding="none"><EmptyState icon={DatabaseBackup} message={tx('Henüz yedekleme işi oluşturulmadı.')} action={create} /></Card>
      : <div className="space-y-4">
        <Filters label={tx('İş filtreleri')} onClear={filtering && sorted.length > 0 ? clear : undefined}
          footer={hasRevoked && <CheckField className="w-fit" label={tx('İptal edilmiş bilgisayarların işlerini göster')} checked={showRevoked} onChange={event => update({ revoked: event.target.checked ? 'true' : '' })} />}>
          <FilterField label={tx('Ara')}>
            <TextInput type="search" aria-label={tx('İş, yedekleme bilgisayarı veya veritabanı ara')} placeholder={tx('İş, bilgisayar veya veritabanı')} value={search} onChange={event => update({ search: event.target.value }, { replace: true })} />
          </FilterField>
          <FilterField label={tx('Durum')}>
            <Select aria-label={tx('Durum')} value={status ?? ''} onChange={event => update({ status: event.target.value })}>
              <option value="">{tx('Tümü')}</option>{statusFilterKeys.map(key => <option key={key} value={key}>{key === 'inactive' ? tx('Pasif') : jobStatusLabel(statusFilters[key][0])}</option>)}
            </Select>
          </FilterField>
          {computers.length > 1 && <FilterField label={tx('Bilgisayar')}>
            <Select aria-label={tx('Bilgisayar')} value={agentId ?? ''} onChange={event => update({ agent: event.target.value })}>
              <option value="">{tx('Tümü')}</option>{computers.map(([id, name]) => <option key={id} value={id}>{name}</option>)}
            </Select>
          </FilterField>}
          {engines.length > 1 && <FilterField label={tx('Veritabanı türü')}>
            <Select aria-label={tx('Veritabanı türü')} value={engine ?? ''} onChange={event => update({ engine: event.target.value })}>
              <option value="">{tx('Tümü')}</option>{engines.map(item => <option key={item} value={item}>{item === 'mssql' ? tx('SQL Server') : item === 'file-system' ? tx('Dosya / Klasör') : 'PostgreSQL'}</option>)}
            </Select>
          </FilterField>}
        </Filters>
        <DataList rows={visible} columns={columns} keyExtractor={row => row.job.id} caption={tx('Yedekleme işleri')}
          sort={{ key: sortKey, direction, onChange: (key, dir) => update(key === 'created' && dir === 'desc' ? { sort: '', dir: '' } : { sort: key, dir }) }}
          emptyMessage={filtering ? tx('Aramanızla eşleşen iş yok.') : tx('Tüm işler iptal edilmiş bilgisayarlara ait.')} onClearFilters={filtering ? clear : undefined} />
        {sorted.length > LIST_PAGE_SIZE && <Pagination label={tx('Yedekleme işi sayfaları')} page={current} pageSize={LIST_PAGE_SIZE} totalCount={sorted.length} onPageChange={next => update({ page: next })} />}
      </div>}
  </QueryState>
}
