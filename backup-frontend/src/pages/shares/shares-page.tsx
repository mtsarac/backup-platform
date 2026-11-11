import { QueryState } from '@/components/data/query-state'
import { PageHeader } from '@/components/ui/page-header'
import { useShares } from '@/features/shares/hooks'
import { SharesTable } from '@/features/shares/shares-table'
import { useTenant } from '@/features/tenancy/use-tenant'
import { tx, useLanguage } from '@/lib/i18n'

export function SharesPage() {
  useLanguage()
  const { tenantId } = useTenant()
  const query = useShares(tenantId)
  const shares = query.data ?? []

  return (
    <div className="space-y-6">
      <PageHeader
        crumbs={[{ label: tx('Paylaşımlarım') }]}
        title={tx('Paylaşımlarım')}
      />

      <QueryState isPending={query.isPending} error={query.error}>
        <SharesTable
          shares={shares}
          caption={tx('Paylaşımlarım')}
          emptyMessage={tx('Henüz paylaşım yapılmamış.')}
          emptyAction={
            <p className="text-sm text-text-muted">
              {tx('Yedek dosyası detayından PlatformAdmin için paylaşım oluşturabilirsiniz.')}
            </p>
          }
        />
      </QueryState>
    </div>
  )
}
