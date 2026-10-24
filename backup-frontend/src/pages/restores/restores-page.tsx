import { tx, useLanguage } from '@/lib/i18n'
import { History } from 'lucide-react'
import { Link, useSearchParams } from 'react-router-dom'
import { DataList, type Column } from '@/components/data/data-list'
import { Pagination } from '@/components/data/pagination'
import { QueryState } from '@/components/data/query-state'
import { RelativeTime } from '@/components/data/relative-time'
import { StatusPill } from '@/components/data/status-pill'
import { Button } from '@/components/ui/button'
import { buttonVariants } from '@/components/ui/button-variants'
import { EmptyState } from '@/components/ui/empty-state'
import { Filters, FilterField, filterControlClass } from '@/components/ui/filters'
import { linkClass } from '@/components/ui/form-styles'
import { Tabs } from '@/components/ui/tabs'
import { ErrorNotice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { useAgents } from '@/features/agents/hooks'
import { BackupPicker } from '@/features/artifacts/backup-picker'
import { useRestores } from '@/features/restores/hooks'
import { restoreModeLabel, restoreStatuses, targetStateLabel, verificationCleanupText } from '@/features/restores/presentation'
import type { RestoreFilters, RestoreStatus, RestoreView } from '@/features/restores/types'
import { useTenant } from '@/features/tenancy/use-tenant'
import { formatDuration } from '@/lib/format'
import { statusLabel } from '@/lib/status'

export function RestoresPage() {
  useLanguage()
  const { tenantId, canOperate } = useTenant()
  const [search, setSearch] = useSearchParams()
  const requestedPage = Number(search.get('page') ?? 1)
  const page = Number.isSafeInteger(requestedPage) && requestedPage > 0 ? requestedPage : 1
  const rawStatus = search.get('status') ?? ''
  const status: RestoreStatus | '' = restoreStatuses.includes(rawStatus as RestoreStatus) ? rawStatus as RestoreStatus : ''
  const verification = search.get('view') === 'tests'
  const filters: RestoreFilters = { page, pageSize: 25, purpose: verification ? 'Verification' : undefined, status, artifactId: search.get('artifactId') ?? '', agentId: search.get('agentId') ?? '' }
  const query = useRestores(filters)
  const agents = useAgents(tenantId)
  const base = `/tenants/${tenantId}`
  const filtering = Boolean(status || filters.artifactId || filters.agentId)
  const emptyFiltered = filtering && query.data?.items.length === 0
  function updateFilters(patch: Record<string, string>) {
    setSearch((current) => {
      const next = new URLSearchParams(current)
      next.delete('page')
      Object.entries(patch).forEach(([key, value]) => { if (value) next.set(key, value); else next.delete(key) })
      return next
    })
  }
  const clearFilters = () => setSearch(verification ? { view: 'tests' } : {})
  const computer = (restore: RestoreView) => agents.data?.find((agent) => agent.agentId === restore.agentId)?.hostName ?? tx('Yedekleme bilgisayarı bulunamadı')
  const sourceBackup = (restore: RestoreView) => <div className="space-y-1">
    <Link className={linkClass} to={`${base}/artifacts/${restore.artifactId}`}>{restore.sourceDatabaseName ?? restore.result?.sourceBackup?.databaseName ?? tx('Yedek dosyasını aç')}</Link>
    {restore.sourceJobName && <p className="text-xs text-text-muted">{restore.sourceJobName}</p>}
  </div>
  const when = { key: 'when', header: tx('Ne zaman'), wrap: true, render: (restore: RestoreView) => <RelativeTime value={restore.createdAt} /> } satisfies Column<RestoreView>
  const testColumns: Column<RestoreView>[] = [
    { key: 'source', header: tx('Test edilen yedek'), wrap: true, render: (restore) => <div className="space-y-1"><Link className={linkClass} to={`${base}/restores/${restore.id}`}>{restore.sourceDatabaseName ?? tx('Geri yükleme testi')}</Link>{restore.sourceJobName && <p className="text-xs text-text-muted">{restore.sourceJobName}</p>}</div> },
    { key: 'outcome', card: 'status', header: tx('Sonuç'), wrap: true, render: (restore) => <div className="space-y-1">{restore.verificationOutcome ? <StatusPill domain="verification" status={restore.verificationOutcome} /> : <StatusPill status={restore.status} />}{restore.cleanup && restore.cleanup.state !== 'Completed' && <p className="text-xs text-text-muted">{verificationCleanupText(restore.cleanup)}</p>}</div> },
    { key: 'agent', header: tx('Yedekleme bilgisayarı'), wrap: true, render: computer },
    when,
    { key: 'duration', header: tx('Süre'), wrap: true, render: (restore) => restore.completedAt ? formatDuration(restore.startedAt, restore.completedAt) : '—' },
  ]
  const columns: Column<RestoreView>[] = [
    { key: 'database', card: 'title', header: tx('Hedef veritabanı'), wrap: true, render: (restore) => <div className="space-y-1"><Link className={linkClass} to={`${base}/restores/${restore.id}`}>{restore.databaseName}</Link><p className="text-sm font-normal">{restore.options.files ? tx('Dosya / klasör geri yükleme') : restoreModeLabel(restore.mode)}</p>{restore.options.files && <p className="break-all text-xs font-normal">{restore.options.files.relativeDirectory || '.'}</p>}</div> },
    { key: 'status', card: 'status', header: tx('Durum'), wrap: true, render: (restore) => <div className="space-y-1"><StatusPill status={restore.status} /><p className="text-xs text-text-muted">{targetStateLabel(restore.targetState)}</p></div> },
    { key: 'source', header: tx('Kaynak yedek'), wrap: true, render: sourceBackup },
    { key: 'agent', header: tx('Yedekleme bilgisayarı'), wrap: true, render: computer },
    when,
  ]
  return <div className="space-y-6">
    <PageHeader title={tx("Geri yükleme geçmişi")} description={tx("Geri yüklemeleri ve hedef veritabanlarının son durumunu buradan izleyin.")} />
    <Tabs label={tx("Liste türü")} idPrefix="restore-view" value={verification ? 'tests' : 'restores'} onChange={(value) => setSearch(value === 'tests' ? { view: 'tests' } : {})} items={[{ value: 'restores', label: tx('Geri yüklemeler') }, { value: 'tests', label: tx('Geri yükleme testleri') }]} />
    <Filters label={tx("Geri yükleme filtreleri")} onClear={emptyFiltered ? undefined : clearFilters}>
      <FilterField label={tx("Durum")}><select aria-label={tx("Durum")} className={filterControlClass} value={status} onChange={(event) => updateFilters({ status: event.target.value })}><option value="">{tx("Tüm durumlar")}</option>{restoreStatuses.map((value) => <option key={value} value={value}>{statusLabel('general', value)}</option>)}</select></FilterField>
      <BackupPicker tenantId={tenantId} value={filters.artifactId ?? ''} onChange={(artifactId) => updateFilters({ artifactId })} />
      <FilterField label={tx("Yedekleme bilgisayarı")}><select aria-label={tx("Yedekleme bilgisayarı")} className={filterControlClass} value={filters.agentId} onChange={(event) => updateFilters({ agentId: event.target.value })} disabled={agents.isPending || Boolean(agents.error)}><option value="">{tx("Tüm yedekleme bilgisayarları")}</option>{filters.agentId && !agents.data?.some((agent) => agent.agentId === filters.agentId) && <option value={filters.agentId}>{tx("Yedekleme bilgisayarı bulunamadı")}</option>}{agents.data?.map((agent) => <option key={agent.agentId} value={agent.agentId}>{agent.hostName ?? tx("Adsız yedekleme bilgisayarı")}</option>)}</select></FilterField>
    </Filters>
    {agents.error && <ErrorNotice title={tx("Yedekleme bilgisayarları yüklenemedi")} error={agents.error} onRetry={() => void agents.refetch()} />}
    <QueryState isPending={query.isPending} error={query.error} onRetry={() => void query.refetch()}>
      {query.data?.items.length === 0
        ? <div data-testid="restores-empty" className="rounded-xl border border-border bg-surface"><EmptyState icon={History} canAct={filtering || canOperate}
          message={filtering ? tx("Bu filtrelere uyan geri yükleme yok.") : verification ? tx("Henüz geri yükleme testi yapılmadı.") : tx("Henüz geri yükleme yapılmadı.")}
          action={filtering ? <Button onClick={clearFilters}>{tx("Filtreleri temizle")}</Button> : <Link className={buttonVariants()} to={`${base}/artifacts`}>{tx("Yedek dosyalarını aç")}</Link>} /></div>
        : <DataList rows={query.data?.items ?? []} columns={verification ? testColumns : columns} keyExtractor={(restore) => restore.id} caption={tx("Geri yüklemeler")} />}
      {query.data && query.data.totalCount > 0 && <Pagination label={tx("Geri yükleme sayfaları")} page={query.data.page} pageSize={query.data.pageSize} totalCount={query.data.totalCount} disabled={query.isFetching} onPageChange={(next) => updateFilters({ page: String(next) })} />}
    </QueryState>
  </div>
}
