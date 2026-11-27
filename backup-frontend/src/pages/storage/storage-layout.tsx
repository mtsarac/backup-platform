import { tx, useLanguage } from '@/lib/i18n'
import { Link, Outlet } from 'react-router-dom'
import { Plus } from 'lucide-react'
import { buttonVariants } from '@/components/ui/button-variants'
import { PageHeader } from '@/components/ui/page-header'
import { Section } from '@/components/ui/section'
import { DataList, type Column } from '@/components/data/data-list'
import { QueryState } from '@/components/data/query-state'
import { linkClass } from '@/components/ui/form-styles'
import { ListPager } from '@/features/lists/list-pager'
import { ListToolbar } from '@/features/lists/list-toolbar'
import { toServerQuery } from '@/features/lists/server-list-query'
import { sortProps, useListState } from '@/features/lists/use-list-state'
import { useTenant } from '@/features/tenancy/use-tenant'
import { useTenantConnections } from '@/features/storage-connections/hooks'
import { useTenantStorageQuota } from '@/features/storage/hooks'
import { QuotaMeter } from '@/features/storage/quota-meter'
import { useArtifacts } from '@/features/artifacts/hooks'
import { formatBytes, formatDateTime } from '@/lib/format'
import type { BackupArtifact } from '@/features/artifacts/types'

// Its own search, sort and page parameters: the connection pages under this layout keep theirs.
const failedListConfig = {
  search: true,
  sort: { first: { createdAt: 'desc', size: 'desc', jobName: 'asc', databaseName: 'asc' }, default: { key: 'createdAt', dir: 'desc' } },
  pageKey: 'failed-page',
  paramPrefix: 'failed-',
} as const

/** The backup is named by its job, else its database; with a run it links to the attempt that produced it. */
const failedColumns = (base: string): Column<BackupArtifact>[] => [
  { key: 'jobName', header: tx('İş'), sortable: true, card: 'title', wrap: true, render: artifact => {
    const name = artifact.jobName || artifact.databaseName
    return artifact.runId ? <Link className={linkClass} to={`${base}/backup-runs/${artifact.runId}`}>{name || tx('Yedekleme denemesi')}</Link> : name || tx('Yedek dosyası')
  } },
  { key: 'databaseName', header: tx('Veritabanı'), sortable: true, wrap: true, render: artifact => artifact.databaseName || '—' },
  { key: 'size', header: tx('Boyut'), sortable: true, render: artifact => formatBytes(artifact.sizeBytes) },
  { key: 'createdAt', header: tx('Oluşturulma'), sortable: true, render: artifact => formatDateTime(artifact.createdAt) },
]

export function StorageLayout() {
  useLanguage()
  const { tenantId } = useTenant()
  const base = `/tenants/${tenantId}`
  const connections = useTenantConnections()
  const quota = useTenantStorageQuota()
  const failedState = useListState(failedListConfig)
  const failed = useArtifacts(tenantId, { ...toServerQuery(failedState), state: 'DeleteFailed' })
  return <div className="space-y-6">
    <PageHeader title={tx('Depolama')} actions={connections.data?.items.length ? <Link to={`${base}/storage/connections/new`} className={buttonVariants()}><Plus aria-hidden="true" className="size-4" />{tx('Depolama bağlantısı ekle')}</Link> : undefined} />
    <QueryState isPending={quota.isPending} error={quota.error} onRetry={() => void quota.refetch()}>
      {quota.data && <Section title={tx('Depolama kullanımı')}>
        <p>{tx('Toplam boyut')}: {formatBytes(quota.data.usedBytes)}{quota.data.limitBytes !== null && <> · {tx('Kota')}: {formatBytes(quota.data.limitBytes)}</>}</p>
        {quota.data.limitBytes !== null && <QuotaMeter usedBytes={quota.data.usedBytes} reservedBytes={quota.data.reservedBytes} limitBytes={quota.data.limitBytes} state={quota.data.state} />}
      </Section>}
    </QueryState>
    <Outlet />
    {failed.isError && <QueryState isPending={false} error={failed.error} onRetry={() => void failed.refetch()} />}
    {failed.data && (failed.data.totalCount > 0 || failedState.filtering) && <Section title={tx('Silinemeyen yedek dosyaları')}>
      <div className="space-y-4">
        <ListToolbar state={failedState} label={tx('Yedek dosyası filtreleri')} total={failed.data.totalCount} search={{ label: tx('Yedek dosyası ara'), placeholder: tx('İş, veritabanı veya dosya adı') }} />
        <DataList caption={tx('Silinemeyen yedek dosyaları')} rows={failed.data.items} columns={failedColumns(base)} keyExtractor={artifact => artifact.id} sort={sortProps(failedState)}
          emptyMessage={tx('Aramanızla eşleşen yedek dosyası yok.')} onClearFilters={failedState.filtering ? failedState.clear : undefined} />
        <ListPager state={failedState} total={failed.data.totalCount} disabled={failed.isFetching} label={tx('Yedek dosyası sayfaları')} />
      </div>
    </Section>}
  </div>
}
