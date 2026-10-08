import { Link, useSearchParams } from 'react-router-dom'
import { DataTable, type Column } from '@/components/data/data-table'
import { Pagination } from '@/components/data/pagination'
import { QueryState } from '@/components/data/query-state'
import { StatusPill } from '@/components/data/status-pill'
import { Button } from '@/components/ui/button'
import { useAgents } from '@/features/agents/hooks'
import { formatTimestamp } from '@/features/backups/presentation'
import { useRestores } from '@/features/restores/hooks'
import { restoreModeLabel, restoreStatuses, restoreStatusLabels, targetStateLabel } from '@/features/restores/presentation'
import type { RestoreFilters, RestoreStatus, RestoreView } from '@/features/restores/types'
import { useTenant } from '@/features/tenancy/use-tenant'
import { errorMessage } from '@/lib/api/failure-messages'

const controlClass = 'h-9 w-full rounded-lg border border-border bg-surface px-3 text-sm text-text focus-visible:outline-2 focus-visible:outline-primary'
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
    { key: 'database', header: 'Hedef Database', render: (restore) => <Link className="font-medium text-primary hover:underline" to={`/tenants/${tenantId}/restores/${restore.id}`}>{restore.databaseName}</Link> },
    { key: 'source', header: 'Kaynak Artifact', wrap: true, render: (restore) => <div><Link className="text-primary hover:underline" to={`/tenants/${tenantId}/artifacts/${restore.artifactId}`}>{restore.sourceDatabaseName ?? restore.result?.sourceBackup?.databaseName ?? 'Artifact'} · {restore.artifactId}</Link>{restore.sourceJobName && <p className="text-xs text-text-muted">{restore.sourceJobName}</p>}</div> },
    { key: 'agent', header: 'Agent', render: (restore) => agents.data?.find((agent) => agent.agentId === restore.agentId)?.hostName ?? restore.agentId },
    { key: 'mode', header: 'Mod', render: (restore) => restoreModeLabel(restore.mode) },
    { key: 'status', header: 'Durum', render: (restore) => <StatusPill status={restore.status} /> },
    { key: 'created', header: 'Oluşturulma', render: (restore) => formatTimestamp(restore.createdAt) },
    { key: 'started', header: 'Başlangıç', render: (restore) => formatTimestamp(restore.startedAt) },
    { key: 'finished', header: 'Bitiş', render: (restore) => formatTimestamp(restore.completedAt) },
    { key: 'targetState', header: 'Hedef Database durumu', wrap: true, render: (restore) => targetStateLabel(restore.targetState) },
  ]
  return <div className="space-y-5">
    <header><h1 className="text-2xl font-semibold text-text-heading">Geri yükleme geçmişi</h1><p className="mt-1 text-sm text-text-muted">Gerçek restore işlemleri ve hedef Database sonuçları. Aktif kayıtlar görünür sekmede güncellenir.</p></header>
    <section aria-label="Geri yükleme filtreleri" className="grid gap-3 rounded-lg border border-border bg-surface p-4 sm:grid-cols-3">
      <label className="space-y-1 text-sm">Durum<select aria-label="Durum" className={controlClass} value={status} onChange={(event) => updateFilters({ status: event.target.value })}><option value="">Tüm durumlar</option>{restoreStatuses.map((value) => <option key={value} value={value}>{restoreStatusLabels[value]}</option>)}</select></label>
      <label className="space-y-1 text-sm">Artifact ID<input aria-label="Artifact ID" className={controlClass} value={filters.artifactId} onChange={(event) => updateFilters({ artifactId: event.target.value })} placeholder="Tüm Artifactlar" /></label>
      <label className="space-y-1 text-sm">Agent<select aria-label="Agent" className={controlClass} value={filters.agentId} onChange={(event) => updateFilters({ agentId: event.target.value })} disabled={agents.isPending || Boolean(agents.error)}><option value="">Tüm Agentlar</option>{filters.agentId && !agents.data?.some((agent) => agent.agentId === filters.agentId) && <option value={filters.agentId}>{filters.agentId}</option>}{agents.data?.map((agent) => <option key={agent.agentId} value={agent.agentId}>{agent.hostName ?? agent.agentId}</option>)}</select></label>
      <div className="sm:col-span-3"><Button variant="outline" onClick={() => setSearch({})}>Filtreleri temizle</Button></div>
      {agents.error && <p role="alert" className="text-sm text-error sm:col-span-3">Agent seçenekleri yüklenemedi: {errorMessage(agents.error)}</p>}
    </section>
    <QueryState isPending={query.isPending} error={query.error}>
      <DataTable rows={query.data?.items ?? []} columns={columns} keyExtractor={(restore) => restore.id} emptyMessage="Bu filtrelerde geri yükleme kaydı bulunamadı." />
      {query.data && query.data.totalCount > 0 && <Pagination label="Geri yükleme sayfaları" page={query.data.page} pageSize={query.data.pageSize} totalCount={query.data.totalCount} disabled={query.isFetching} onPageChange={(next) => updateFilters({ page: String(next) })} />}
    </QueryState>
    {query.error && <Button variant="outline" onClick={() => void query.refetch()}>Tekrar dene</Button>}
  </div>
}
