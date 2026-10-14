import { Link, useSearchParams } from 'react-router-dom'
import { DataTable, type Column } from '@/components/data/data-table'
import { Pagination } from '@/components/data/pagination'
import { QueryState } from '@/components/data/query-state'
import { StatusPill } from '@/components/data/status-pill'
import { Button } from '@/components/ui/button'
import { EmptyState } from '@/components/ui/empty-state'
import { Filters, FilterField, filterControlClass } from '@/components/ui/filters'
import { Notice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { useAgents } from '@/features/agents/hooks'
import { formatTimestamp } from '@/features/backups/presentation'
import { useRestores } from '@/features/restores/hooks'
import { restoreModeLabel, restoreStatuses, restoreStatusLabels, targetStateLabel } from '@/features/restores/presentation'
import type { RestoreFilters, RestoreStatus, RestoreView } from '@/features/restores/types'
import { useTenant } from '@/features/tenancy/use-tenant'
import { errorMessage } from '@/lib/api/failure-messages'

export function RestoresPage() {
  const { tenantId } = useTenant()
  const [search, setSearch] = useSearchParams()
  const requestedPage = Number(search.get('page') ?? 1)
  const page = Number.isSafeInteger(requestedPage) && requestedPage > 0 ? requestedPage : 1
  const rawStatus = search.get('status') ?? ''
  const status: RestoreStatus | '' = restoreStatuses.includes(rawStatus as RestoreStatus) ? rawStatus as RestoreStatus : ''
  const filters: RestoreFilters = { page, pageSize: 25, status, artifactId: search.get('artifactId') ?? '', agentId: search.get('agentId') ?? '' }
  const query = useRestores(filters)
  const agents = useAgents(tenantId)
  function updateFilters(patch: Record<string, string>) {
    setSearch((current) => {
      const next = new URLSearchParams(current)
      next.delete('page')
      Object.entries(patch).forEach(([key, value]) => { if (value) next.set(key, value); else next.delete(key) })
      return next
    })
  }
  const columns: Column<RestoreView>[] = [
    { key: 'database', header: 'Hedef Database', wrap: true, render: (restore) => <div className="space-y-1"><Link className="font-medium text-primary hover:underline" to={`/tenants/${tenantId}/restores/${restore.id}`}>{restore.databaseName}</Link><p className="text-sm">{restoreModeLabel(restore.mode)}</p><p className="text-xs text-text-muted">{agents.data?.find((agent) => agent.agentId === restore.agentId)?.hostName ?? 'Agent adı bulunamadı'}</p></div> },
    { key: 'source', header: 'Kaynak Artifact', wrap: true, render: (restore) => <div className="space-y-1"><Link className="text-primary hover:underline" to={`/tenants/${tenantId}/artifacts/${restore.artifactId}`}>{restore.sourceDatabaseName ?? restore.result?.sourceBackup?.databaseName ?? 'Artifact ayrıntıları'}</Link>{restore.sourceJobName && <p className="text-xs text-text-muted">{restore.sourceJobName}</p>}<p className="break-all font-mono text-xs text-text-muted">{restore.artifactId}</p></div> },
    { key: 'status', header: 'Durum', wrap: true, render: (restore) => <div className="space-y-1"><StatusPill status={restore.status} /><p className="text-xs text-text-muted">{targetStateLabel(restore.targetState)}</p></div> },
    { key: 'created', header: 'Oluşturulma', wrap: true, render: (restore) => formatTimestamp(restore.createdAt) },
    { key: 'times', header: 'Çalışma zamanı', wrap: true, render: (restore) => <div className="space-y-1 text-sm"><p>Başlangıç: {formatTimestamp(restore.startedAt)}</p><p className="text-text-muted">Bitiş: {formatTimestamp(restore.completedAt)}</p></div> },
  ]
  return <div className="space-y-6">
    <PageHeader title="Geri yükleme geçmişi" description="Restore işlemlerini ve hedef Database sonuçlarını izleyin. Aktif kayıtlar görünür sekmede güncellenir." />
    <Filters label="Geri yükleme filtreleri" onClear={() => setSearch({})}>
      <FilterField label="Durum"><select aria-label="Durum" className={filterControlClass} value={status} onChange={(event) => updateFilters({ status: event.target.value })}><option value="">Tüm durumlar</option>{restoreStatuses.map((value) => <option key={value} value={value}>{restoreStatusLabels[value]}</option>)}</select></FilterField>
      <FilterField label="Artifact ID"><input aria-label="Artifact ID" className={filterControlClass} value={filters.artifactId} onChange={(event) => updateFilters({ artifactId: event.target.value })} placeholder="Tüm Artifactlar" /></FilterField>
      <FilterField label="Agent"><select aria-label="Agent" className={filterControlClass} value={filters.agentId} onChange={(event) => updateFilters({ agentId: event.target.value })} disabled={agents.isPending || Boolean(agents.error)}><option value="">Tüm Agentlar</option>{filters.agentId && !agents.data?.some((agent) => agent.agentId === filters.agentId) && <option value={filters.agentId}>{filters.agentId}</option>}{agents.data?.map((agent) => <option key={agent.agentId} value={agent.agentId}>{agent.hostName ?? agent.agentId}</option>)}</select></FilterField>
    </Filters>
    {agents.error && <Notice tone="error">Agent seçenekleri yüklenemedi: {errorMessage(agents.error)}</Notice>}
    <QueryState isPending={query.isPending} error={query.error}>
      {query.data?.items.length === 0 ? <EmptyState title="Bu filtrelerde geri yükleme kaydı bulunamadı." action={<Link className="font-medium text-primary hover:underline" to={`/tenants/${tenantId}/artifacts`}>Artifact'ları aç</Link>}>Geri yüklemeyi kullanılabilir bir Artifact üzerinden başlatın veya filtreleri değiştirin.</EmptyState> : <DataTable rows={query.data?.items ?? []} columns={columns} keyExtractor={(restore) => restore.id} emptyMessage="Bu filtrelerde geri yükleme kaydı bulunamadı." />}
      {query.data && query.data.totalCount > 0 && <Pagination label="Geri yükleme sayfaları" page={query.data.page} pageSize={query.data.pageSize} totalCount={query.data.totalCount} disabled={query.isFetching} onPageChange={(next) => updateFilters({ page: String(next) })} />}
    </QueryState>
    {query.error && <Button variant="outline" onClick={() => void query.refetch()}>Tekrar dene</Button>}
  </div>
}
