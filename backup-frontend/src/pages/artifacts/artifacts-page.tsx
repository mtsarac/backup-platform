import { useState } from 'react'
import { Archive } from 'lucide-react'
import { Link } from 'react-router-dom'
import { DataTable } from '@/components/data/data-table'
import type { Column } from '@/components/data/data-table'
import { Pagination } from '@/components/data/pagination'
import { QueryState } from '@/components/data/query-state'
import { StatusPill } from '@/components/data/status-pill'
import { EmptyState } from '@/components/ui/empty-state'
import { FilterField, Filters, filterControlClass } from '@/components/ui/filters'
import { Notice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { artifactDate, artifactSize, artifactState } from '@/features/artifacts/format'
import { useArtifacts } from '@/features/artifacts/hooks'
import type { ArtifactFilters, ArtifactState, BackupArtifact } from '@/features/artifacts/types'
import { ReplicationSummary } from '@/features/backups/replication'
import { linkClass } from '@/features/platform/styles'
import { useTenant } from '@/features/tenancy/use-tenant'

const stateOptions: { value: ArtifactState | ''; label: string }[] = [
  { value: '', label: 'Tüm durumlar' },
  { value: 'Available', label: 'Kullanılabilir' },
  { value: 'DeletePending', label: 'Silme bekliyor' },
  { value: 'DeleteFailed', label: 'Silme başarısız' },
  { value: 'Deleted', label: 'Silinmiş · geçmiş' },
]
const initialFilter: ArtifactFilters = { page: 1, pageSize: 25, state: '', from: '', to: '' }

export function ArtifactsPage() {
  const { tenantId } = useTenant()
  const [filter, setFilter] = useState<ArtifactFilters>(initialFilter)
  const query = useArtifacts(tenantId, filter)
  const invalidDates = Boolean(filter.from && filter.to && filter.from > filter.to)
  const columns: Column<BackupArtifact>[] = [
    { key: 'database', header: 'Backup', wrap: true, render: (artifact) => <div className="space-y-0.5">
      <Link className={linkClass} to={`/tenants/${tenantId}/artifacts/${artifact.id}`}>{artifact.databaseName || 'Backup detayları'}</Link>
      <p className="text-sm text-text-muted">{artifact.jobId ? <Link className="hover:underline" to={`/tenants/${tenantId}/backup-jobs`}>{artifact.jobName || 'Backup Job listesi'}</Link> : 'Backup Job yok'}{artifact.runId && <> · <Link className="hover:underline" to={`/tenants/${tenantId}/backup-runs/${artifact.runId}`}>Çalıştırmayı aç</Link></>}</p>
    </div> },
    { key: 'state', header: 'Durum', render: (artifact) => <StatusPill status={artifactState(artifact)} /> },
    { key: 'size', header: 'Boyut', render: (artifact) => artifactSize(artifact.sizeBytes) },
    { key: 'created', header: 'Oluşturulma', render: (artifact) => <div><p>{artifactDate(artifact.createdAt)}</p><p className="text-sm text-text-muted">{artifact.retentionExpiresAt ? `Saklama sonu: ${artifactDate(artifact.retentionExpiresAt)}` : 'Süre sınırı yok'}</p></div> },
    { key: 'replicas', header: 'Replika', wrap: true, render: (artifact) => <div className="space-y-1"><ReplicationSummary replication={artifact.replication} /><div className="flex flex-wrap gap-1">{artifact.replicas.map((replica, index) => <span key={replica.id} className="flex items-center gap-1"><span className="text-sm text-text-muted">{index + 1}.</span><StatusPill status={replica.status} /></span>)}</div></div> },
  ]
  function setFilters(next: Partial<ArtifactFilters>) {
    setFilter((current) => ({ ...current, ...next, page: 1 }))
  }
  return (
    <div className="space-y-6">
      <PageHeader title="Backup dosyaları" description="Kullanılabilir Backup dosyaları ve silinmiş dosyaların kalıcı geçmişi." />
      <Filters label="Backup filtreleri" onClear={() => setFilter(initialFilter)}>
        <FilterField label="Durum"><select aria-label="Durum" className={filterControlClass} value={filter.state} onChange={(event) => setFilters({ state: event.target.value as ArtifactState | '' })}>{stateOptions.map((item) => <option key={item.value} value={item.value}>{item.label}</option>)}</select></FilterField>
        <FilterField label="Başlangıç tarihi"><input type="date" className={filterControlClass} value={filter.from} onChange={(event) => setFilters({ from: event.target.value })} /></FilterField>
        <FilterField label="Bitiş tarihi"><input type="date" className={filterControlClass} value={filter.to} min={filter.from || undefined} onChange={(event) => setFilters({ to: event.target.value })} /></FilterField>
      </Filters>
      {invalidDates && <Notice tone="error">Bitiş tarihi başlangıç tarihinden önce olamaz.</Notice>}
      {!invalidDates && <QueryState isPending={query.isPending} error={query.error}>
        {query.data?.items.length === 0
          ? <div className="rounded-xl border border-border bg-surface"><EmptyState icon={Archive} title="Kayıt bulunamadı.">Bu filtrelerde Backup dosyası yok. Filtreleri temizleyin veya bir Backup Job çalıştırın; tamamlanan Backup Run burada görünür.</EmptyState></div>
          : <DataTable rows={query.data?.items ?? []} columns={columns} keyExtractor={(artifact) => artifact.id} />}
      </QueryState>}
      {query.data && !invalidDates && query.data.totalCount > 0 && <Pagination label="Backup sayfalama" page={filter.page} pageSize={filter.pageSize} totalCount={query.data.totalCount} disabled={query.isFetching} onPageChange={(page) => setFilter((current) => ({ ...current, page }))} />}
    </div>
  )
}
