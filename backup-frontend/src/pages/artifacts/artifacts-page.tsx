import { useState } from 'react'
import { Link } from 'react-router-dom'
import { DataTable } from '@/components/data/data-table'
import type { Column } from '@/components/data/data-table'
import { QueryState } from '@/components/data/query-state'
import { StatusPill } from '@/components/data/status-pill'
import { Button } from '@/components/ui/button'
import { artifactDate, artifactSize, artifactState } from '@/features/artifacts/format'
import { useArtifacts } from '@/features/artifacts/hooks'
import type { ArtifactFilters, ArtifactState, BackupArtifact } from '@/features/artifacts/types'
import { useTenant } from '@/features/tenancy/use-tenant'

const filters: { value: ArtifactState | ''; label: string }[] = [
  { value: '', label: 'Tüm durumlar' },
  { value: 'Available', label: 'Kullanılabilir' },
  { value: 'DeletePending', label: 'Silme bekliyor' },
  { value: 'DeleteFailed', label: 'Silme başarısız' },
  { value: 'Deleted', label: 'Silinmiş · geçmiş' },
]
const inputClass = 'h-9 rounded-md border border-border bg-surface px-3 text-sm text-text focus-visible:outline-2 focus-visible:outline-primary'

export function ArtifactsPage() {
  const { tenantId } = useTenant()
  const [filter, setFilter] = useState<ArtifactFilters>({ page: 1, pageSize: 25, state: '', from: '', to: '' })
  const query = useArtifacts(tenantId, filter)
  const invalidDates = Boolean(filter.from && filter.to && filter.from > filter.to)
  const columns: Column<BackupArtifact>[] = [
    { key: 'database', header: 'Database', render: (artifact) => <Link className="font-medium text-primary hover:underline" to={`/tenants/${tenantId}/artifacts/${artifact.id}`}>{artifact.databaseName || 'Backup detayları'}</Link> },
    { key: 'job', header: 'Backup Job', render: (artifact) => artifact.jobId ? <Link className="text-primary hover:underline" to={`/tenants/${tenantId}/backup-jobs`}>{artifact.jobName || 'Backup Job listesi'}</Link> : '—' },
    { key: 'run', header: 'Çalıştırma', render: (artifact) => artifact.runId ? <Link className="text-primary hover:underline" to={`/tenants/${tenantId}/backup-runs/${artifact.runId}`}>Çalıştırmayı aç</Link> : '—' },
    { key: 'created', header: 'Oluşturulma', render: (artifact) => artifactDate(artifact.createdAt) },
    { key: 'size', header: 'Boyut', render: (artifact) => artifactSize(artifact.sizeBytes) },
    { key: 'expiry', header: 'Saklama sonu', render: (artifact) => artifact.retentionExpiresAt ? artifactDate(artifact.retentionExpiresAt) : 'Süre sınırı yok' },
    { key: 'state', header: 'Durum', render: (artifact) => <StatusPill status={artifactState(artifact)} /> },
    { key: 'replicas', header: 'Replika durumu', render: (artifact) => <div className="flex flex-wrap gap-1">{artifact.replicas.map((replica, index) => <span key={replica.id} className="flex items-center gap-1"><span className="text-xs text-text-muted">{index + 1}.</span><StatusPill status={replica.status} /></span>)}</div> },
  ]
  const lastPage = Math.max(1, Math.ceil((query.data?.totalCount ?? 0) / filter.pageSize))
  function setFilters(next: Partial<ArtifactFilters>) {
    setFilter((current) => ({ ...current, ...next, page: 1 }))
  }
  return (
    <div className="space-y-6">
      <header><h1 className="text-2xl font-semibold text-text">Backup dosyaları</h1><p className="mt-1 text-sm text-text-muted">Kullanılabilir Backup dosyaları ve silinmiş dosyaların kalıcı geçmişi.</p></header>
      <div className="flex flex-wrap items-end gap-4 rounded-lg border border-border bg-surface p-4" aria-label="Backup filtreleri">
        <label className="grid gap-1 text-sm text-text-muted">Durum<select className={inputClass} value={filter.state} onChange={(event) => setFilters({ state: event.target.value as ArtifactState | '' })}>{filters.map((item) => <option key={item.value} value={item.value}>{item.label}</option>)}</select></label>
        <label className="grid gap-1 text-sm text-text-muted">Başlangıç tarihi<input type="date" className={inputClass} value={filter.from} onChange={(event) => setFilters({ from: event.target.value })} /></label>
        <label className="grid gap-1 text-sm text-text-muted">Bitiş tarihi<input type="date" className={inputClass} value={filter.to} min={filter.from || undefined} onChange={(event) => setFilters({ to: event.target.value })} /></label>
        <Button variant="outline" onClick={() => setFilter({ page: 1, pageSize: 25, state: '', from: '', to: '' })}>Filtreleri temizle</Button>
      </div>
      {invalidDates && <p role="alert" className="text-sm text-destructive">Bitiş tarihi başlangıç tarihinden önce olamaz.</p>}
      {!invalidDates && <QueryState isPending={query.isPending} error={query.error} empty={query.data?.items.length === 0}>
        <DataTable rows={query.data?.items ?? []} columns={columns} keyExtractor={(artifact) => artifact.id} />
      </QueryState>}
      {query.data && !invalidDates && query.data.totalCount > 0 && <nav className="flex flex-wrap items-center justify-between gap-3 text-sm text-text-muted" aria-label="Backup sayfalama">
        <span>{query.data.totalCount} kayıt · Sayfa {filter.page} / {lastPage}</span>
        <div className="flex gap-2"><Button variant="outline" disabled={filter.page <= 1 || query.isFetching} onClick={() => setFilter((current) => ({ ...current, page: current.page - 1 }))}>Önceki</Button><Button variant="outline" disabled={filter.page >= lastPage || query.isFetching} onClick={() => setFilter((current) => ({ ...current, page: current.page + 1 }))}>Sonraki</Button></div>
      </nav>}
    </div>
  )
}
