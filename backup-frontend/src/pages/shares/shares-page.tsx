import { QueryState } from '@/components/data/query-state'
import { Link } from 'react-router-dom'
import { buttonVariants } from '@/components/ui/button-variants'
import { Section } from '@/components/ui/section'
import { useShares, useTenantDownloadAttempts } from '@/features/shares/hooks'
import { DownloadAttemptsTable, SharesTable } from '@/features/shares/shares-table'
import { useTenant } from '@/features/tenancy/use-tenant'
import { tx, useLanguage } from '@/lib/i18n'

export function SharesPage() {
  useLanguage()
  const { tenantId, canAdmin } = useTenant()
  const query = useShares(tenantId)
  const attemptsQuery = useTenantDownloadAttempts(tenantId)
  const shares = query.data ?? []
  const attempts = attemptsQuery.data?.pages.flatMap((page) => page.items) ?? []
  return (
    <div className="space-y-6">
      <QueryState isPending={query.isPending} error={query.error}>
        <SharesTable
          shares={shares}
          canAdmin={canAdmin}
          caption={tx('Paylaşımlarım')}
          emptyMessage={tx('Henüz paylaşım yapılmamış.')}
          emptyAction={<Link className={buttonVariants()} to={`/tenants/${tenantId}/backup-runs`}>{tx('Yedek dosyalarını aç')}</Link>}
        />
      </QueryState>

      <details className="group">
        <summary className="w-fit cursor-pointer rounded-sm py-1 font-medium text-primary underline-offset-4 hover:underline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring">{tx('İndirme girişimleri')}</summary>
      <Section
        title={tx('İndirme girişimleri')}
        description={tx('Çalışma alanınıza ait paylaşımlar ve yedek dosyaları üzerindeki indirme girişimleri.')}
      >
        <QueryState isPending={attemptsQuery.isPending} error={attemptsQuery.error}>
          <DownloadAttemptsTable attempts={attempts} onRefresh={() => void attemptsQuery.refetch()} onLoadMore={attemptsQuery.hasNextPage ? () => { void attemptsQuery.fetchNextPage() } : undefined} loadingMore={attemptsQuery.isFetchingNextPage} />
        </QueryState>
      </Section>
      </details>
    </div>
  )
}
