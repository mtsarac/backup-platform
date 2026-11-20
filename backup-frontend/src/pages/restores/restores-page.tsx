import { tx, useLanguage } from '@/lib/i18n'
import { Badge } from '@/components/ui/badge'
import { Select } from '@/components/ui/text-input'
import { History } from 'lucide-react'
import { Link } from 'react-router-dom'
import { DataList, type Column } from '@/components/data/data-list'
import { QueryState } from '@/components/data/query-state'
import { RelativeTime } from '@/components/data/relative-time'
import { StatusPill } from '@/components/data/status-pill'
import { buttonVariants } from '@/components/ui/button-variants'
import { Disclosure } from '@/components/ui/disclosure'
import { Filters, FilterField } from '@/components/ui/filters'
import { linkClass } from '@/components/ui/form-styles'
import { ErrorNotice } from '@/components/ui/notice'
import { SearchablePicker } from '@/components/ui/searchable-picker'
import { useAgents } from '@/features/agents/hooks'
import { BackupPicker } from '@/features/artifacts/backup-picker'
import { ArtifactRunLink } from '@/features/artifacts/artifact-run-link'
import { useRestores } from '@/features/restores/hooks'
import { restoreModeLabel, restoreStatuses, targetStateLabel } from '@/features/restores/presentation'
import { restoreTitle, sourceBackupLabel, useSourceDatabaseName } from '@/features/restores/source-backup'
import type { RestoreView } from '@/features/restores/types'
import { ListEmpty } from '@/features/lists/list-empty'
import { ListPager } from '@/features/lists/list-pager'
import { ListToolbar } from '@/features/lists/list-toolbar'
import { toServerQuery } from '@/features/lists/server-list-query'
import { sortProps, useListState } from '@/features/lists/use-list-state'
import { useTenant } from '@/features/tenancy/use-tenant'
import { statusLabel } from '@/lib/status'

const listConfig = {
  search: true,
  filters: { status: restoreStatuses, artifactId: 'text', agentId: 'text' },
  sort: { first: { createdAt: 'desc', status: 'asc', target: 'asc', source: 'asc', computer: 'asc' }, default: { key: 'createdAt', dir: 'desc' } },
} as const

function RestoreLink({ restore, to }: { restore: RestoreView; to: string }) {
  const { name } = useSourceDatabaseName(restore)
  return <Link className={linkClass} to={to}>{restoreTitle(restore, name)}</Link>
}

function SourceBackup({ restore, tenantId }: { restore: RestoreView; tenantId: string }) {
  const { name, createdAt } = useSourceDatabaseName(restore)
  return <div className="space-y-1">
    <ArtifactRunLink className={linkClass} tenantId={tenantId} artifactId={restore.artifactId}>{sourceBackupLabel(name, createdAt)}</ArtifactRunLink>
    {restore.sourceJobName && <p className="text-xs text-text-muted">{restore.sourceJobName}</p>}
  </div>
}

export function RestoresPage() {
  useLanguage()
  const { tenantId } = useTenant()
  const state = useListState(listConfig)
  const { status, artifactId, agentId } = state.filters
  const query = useRestores({ ...toServerQuery(state), purpose: 'All' })
  const agents = useAgents(tenantId, true)
  const base = `/tenants/${tenantId}`
  const filtering = state.filtering
  const computer = (restore: RestoreView) => agents.data?.find((agent) => agent.agentId === restore.agentId)?.hostName ?? tx('Yedekleme bilgisayarı bulunamadı')
  const sourceBackup = (restore: RestoreView) => <SourceBackup restore={restore} tenantId={tenantId} />
  const when = { key: 'createdAt', sortable: true, header: tx('Ne zaman'), wrap: true, render: (restore: RestoreView) => <RelativeTime value={restore.createdAt} /> } satisfies Column<RestoreView>
  const columns: Column<RestoreView>[] = [
    { key: 'target', sortable: true, card: 'title', header: tx('Hedef veritabanı'), wrap: true, render: (restore) => <div className="space-y-1"><RestoreLink restore={restore} to={`${base}/restores/${restore.id}`} />{restore.purpose === 'Verification' && <Badge tone="accent">{tx('Deneme')}</Badge>}<p className="text-sm font-normal">{restore.options.files ? tx('Dosya / klasör geri yükleme') : restoreModeLabel(restore.mode)}</p>{restore.options.files && <p className="break-all text-xs font-normal">{restore.options.files.relativeDirectory || '.'}</p>}</div> },
    { key: 'status', sortable: true, card: 'status', header: tx('Durum'), wrap: true, render: (restore) => <div className="space-y-1"><StatusPill status={restore.status} /><p className="text-xs text-text-muted">{targetStateLabel(restore.targetState, Boolean(restore.options.files))}</p></div> },
    { key: 'source', sortable: true, header: tx('Kaynak yedek'), wrap: true, render: sourceBackup },
    { key: 'computer', sortable: true, header: tx('Yedekleme bilgisayarı'), wrap: true, render: computer },
    when,
  ]
  return <div className="space-y-6">
    <ListToolbar state={state} label={tx("Geri yükleme filtreleri")} total={query.data?.totalCount} search={{ label: tx('Ara') }}>
      <FilterField label={tx("Durum")}><Select aria-label={tx("Durum")} value={status} onChange={(event) => state.setFilter('status', event.target.value)}><option value="">{tx("Tüm durumlar")}</option>{restoreStatuses.map((value) => <option key={value} value={value}>{statusLabel('general', value)}</option>)}</Select></FilterField>
    </ListToolbar>
    <Disclosure title={tx('Diğer filtreler')} open={Boolean(artifactId || agentId)}>
      <Filters label={tx('Diğer filtreler')}>
      <BackupPicker tenantId={tenantId} value={artifactId} onChange={value => state.setFilter('artifactId', value)} />
      <SearchablePicker label={tx("Yedekleme bilgisayarı")} placeholder={tx("Tüm yedekleme bilgisayarları")} value={agentId} onChange={value => state.setFilter('agentId', value)}
        options={(agents.data ?? []).map(agent => ({ value: agent.agentId, label: agent.hostName ?? tx("Adsız yedekleme bilgisayarı") }))}
        selectedLabel={agentId ? tx("Yedekleme bilgisayarı bulunamadı") : undefined} disabled={agents.isPending || Boolean(agents.error)} />
    </Filters>
    </Disclosure>
    {agents.error && <ErrorNotice title={tx("Yedekleme bilgisayarları yüklenemedi")} error={agents.error} onRetry={() => void agents.refetch()} />}
    <QueryState isPending={query.isPending} error={query.error} onRetry={() => void query.refetch()}>
      {query.data?.totalCount === 0
        ? <ListEmpty icon={History} filtering={filtering} onClear={state.clear} filteredMessage={tx("Bu filtrelere uyan geri yükleme yok.")} message={tx("Henüz geri yükleme yapılmadı. Bir yedeğin ayrıntısından geri yükleyebilirsiniz.")}
          action={<Link className={buttonVariants()} to={`${base}/backup-runs`}>{tx("Yedek dosyalarını aç")}</Link>} />
        : <DataList rows={query.data?.items ?? []} columns={columns} sort={sortProps(state)} keyExtractor={(restore) => restore.id} caption={tx("Geri yüklemeler")} emptyMessage={tx("Henüz geri yükleme yapılmadı. Bir yedeğin ayrıntısından geri yükleyebilirsiniz.")} />}
      {query.data && <ListPager state={state} total={query.data.totalCount} disabled={query.isFetching} label={tx("Geri yükleme sayfaları")} />}
    </QueryState>
  </div>
}
