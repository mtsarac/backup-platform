import { tx, useLanguage } from '@/lib/i18n'
import { Select, TextInput } from '@/components/ui/text-input'
import { Archive } from 'lucide-react'
import { Link } from 'react-router-dom'
import { DataList } from '@/components/data/data-list'
import type { Column } from '@/components/data/data-list'
import { Pagination } from '@/components/data/pagination'
import { QueryState } from '@/components/data/query-state'
import { StatusPill } from '@/components/data/status-pill'
import { Card } from '@/components/ui/card'
import { EmptyState } from '@/components/ui/empty-state'
import { FilterField, Filters } from '@/components/ui/filters'
import { Notice } from '@/components/ui/notice'
import { artifactState } from '@/features/artifacts/format'
import { useArtifacts } from '@/features/artifacts/hooks'
import type { ArtifactFilters, BackupArtifact } from '@/features/artifacts/types'
import { ReplicationSummary } from '@/features/backups/replication'
import { LIST_PAGE_SIZE, dayParam, oneOf, useListParams } from '@/features/platform/use-list-params'
import { linkClass } from '@/components/ui/form-styles'
import { useTenant } from '@/features/tenancy/use-tenant'
import { formatBytes, formatDateTime } from '@/lib/format'

const stateOptions = [
  { value: '', get label() { return tx("Tüm durumlar") } },
  { value: 'Available', get label() { return tx("Kullanılabilir") } },
  { value: 'DeletePending', get label() { return tx("Silme bekliyor") } },
  { value: 'DeleteFailed', get label() { return tx("Silme başarısız") } },
  { value: 'Deleted', get label() { return tx("Silinmiş · geçmiş") } },
] as const
const states = stateOptions.flatMap((option) => option.value ? [option.value] : [])

export function ArtifactsPage() {
  useLanguage()
  const { tenantId } = useTenant()
  const { page, get, update } = useListParams()
  const filter: ArtifactFilters = { page, pageSize: LIST_PAGE_SIZE, state: oneOf(get('state'), states) ?? '', from: dayParam(get('from')), to: dayParam(get('to')) }
  const query = useArtifacts(tenantId, filter)
  const invalidDates = Boolean(filter.from && filter.to && filter.from > filter.to)
  const filtering = Boolean(filter.state || filter.from || filter.to)
  const base = `/tenants/${tenantId}`
  const columns: Column<BackupArtifact>[] = [
    { key: 'database', header: tx('Backup'), wrap: true, render: (artifact) => <div className="space-y-0.5">
      <Link className={linkClass} to={`${base}/artifacts/${artifact.id}`}>{artifact.databaseName || tx('Backup detayları')}</Link>
      <p className="text-sm text-text-muted">{artifact.jobId ? <Link className="hover:underline" to={`${base}/backup-jobs/${artifact.jobId}/edit`}>{artifact.jobName || tx('Yedekleme işi')}</Link> : tx('Backup Job yok')}{artifact.runId && <> · <Link className="hover:underline" to={`${base}/backup-runs/${artifact.runId}`}>{tx("Çalıştırmayı aç")}</Link></>} · <Link className="hover:underline" to={`${base}/restores?artifactId=${encodeURIComponent(artifact.id)}`}>{tx("Bu yedeğin geri yüklemeleri")}</Link></p>
    </div> },
    { key: 'state', card: 'status', header: tx('Durum'), render: (artifact) => <StatusPill status={artifactState(artifact)} /> },
    { key: 'size', header: tx('Boyut'), render: (artifact) => formatBytes(artifact.sizeBytes) },
    { key: 'encryption', card: 'hidden', header: tx('Şifreleme'), render: (artifact) => artifact.encryption ? <StatusPill level="safe">{tx("Şifreli · v2-bpe")}</StatusPill> : <span className="text-text-muted">{tx("Şifresiz · v1")}</span> },
    { key: 'created', header: tx('Oluşturulma'), render: (artifact) => <div><p>{formatDateTime(artifact.createdAt)}</p><p className="text-sm text-text-muted">{artifact.retentionExpiresAt ? tx("Saklama sonu: {0}", { "0": formatDateTime(artifact.retentionExpiresAt) }) : tx('Süre sınırı yok')}</p></div> },
    { key: 'replicas', header: tx('Replika'), wrap: true, render: (artifact) => <div className="space-y-1"><ReplicationSummary replication={artifact.replication} /><div className="flex flex-wrap gap-1">{artifact.replicas.map((replica, index) => <span key={replica.id} className="flex items-center gap-1"><span className="text-sm text-text-muted">{index + 1}.</span><StatusPill status={replica.status} /></span>)}</div></div> },
  ]
  return (
    <div className="space-y-6">
      <Filters label={tx("Backup filtreleri")} onClear={filtering ? () => update({ state: '', from: '', to: '' }) : undefined}>
        <FilterField label={tx("Durum")}><Select aria-label={tx("Durum")} value={filter.state} onChange={(event) => update({ state: event.target.value })}>{stateOptions.map((item) => <option key={item.value} value={item.value}>{tx(item.label)}</option>)}</Select></FilterField>
        <FilterField label={tx("Başlangıç tarihi")}><TextInput type="date" value={filter.from} onChange={(event) => update({ from: event.target.value })} /></FilterField>
        <FilterField label={tx("Bitiş tarihi")}><TextInput type="date" value={filter.to} min={filter.from || undefined} onChange={(event) => update({ to: event.target.value })} /></FilterField>
      </Filters>
      {invalidDates && <Notice tone="warning">{tx("Bitiş tarihi başlangıç tarihinden önce olamaz.")}</Notice>}
      {!invalidDates && <QueryState isPending={query.isPending} error={query.error}>
        {query.data?.items.length === 0
          ? <Card padding="none"><EmptyState icon={Archive} message={tx("Bu filtrelerde Backup dosyası yok; filtreleri temizleyin veya bir Backup Job çalıştırın.")} /></Card>
          : <DataList rows={query.data?.items ?? []} columns={columns} keyExtractor={(artifact) => artifact.id} />}
      </QueryState>}
      {query.data && !invalidDates && query.data.totalCount > 0 && <Pagination label={tx("Backup sayfalama")} page={filter.page} pageSize={filter.pageSize} totalCount={query.data.totalCount} disabled={query.isFetching} onPageChange={(next) => update({ page: next })} />}
    </div>
  )
}
