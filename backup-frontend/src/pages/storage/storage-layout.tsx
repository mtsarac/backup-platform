import { tx, useLanguage } from '@/lib/i18n'
import { Link, Outlet } from 'react-router-dom'
import { Plus } from 'lucide-react'
import { buttonVariants } from '@/components/ui/button-variants'
import { PageHeader } from '@/components/ui/page-header'
import { Section } from '@/components/ui/section'
import { QueryState } from '@/components/data/query-state'
import { ListPager } from '@/features/lists/list-pager'
import { useListState } from '@/features/lists/use-list-state'
import { useTenant } from '@/features/tenancy/use-tenant'
import { useTenantConnections } from '@/features/storage-connections/hooks'
import { useTenantStorageQuota } from '@/features/storage/hooks'
import { QuotaMeter } from '@/features/storage/quota-meter'
import { useArtifacts } from '@/features/artifacts/hooks'
import { formatBytes } from '@/lib/format'

// Paging only: the artifacts endpoint resolves job and database names after it has read the page (no search or name sort without joining runs and jobs) and the list is the short exception list of deletions that failed; a date sort would sort a column nobody sees.
const failedListConfig = { pageKey: 'failedPage', sort: { first: { createdAt: 'desc' }, default: { key: 'createdAt', dir: 'desc' } } } as const

export function StorageLayout() {
  useLanguage()
  const { tenantId } = useTenant()
  const base = `/tenants/${tenantId}`
  const connections = useTenantConnections()
  const quota = useTenantStorageQuota()
  const failedState = useListState(failedListConfig)
  const failed = useArtifacts(tenantId, { page: failedState.page, pageSize: failedState.pageSize, state: 'DeleteFailed', from: '', to: '' })
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
    {failed.data && failed.data.totalCount > 0 && <Section title={tx('Silinemeyen yedek dosyaları')}>
      <ul className="space-y-2">{failed.data.items.map((artifact) => <li key={artifact.id}>{artifact.runId ? <Link className="underline" to={`${base}/backup-runs/${artifact.runId}`}>{artifact.jobName || artifact.databaseName || tx('Yedekleme denemesi')}</Link> : (artifact.jobName || artifact.databaseName || tx('Yedek dosyası'))}</li>)}</ul>
      <ListPager state={failedState} total={failed.data.totalCount} disabled={failed.isFetching} />
    </Section>}
  </div>
}
