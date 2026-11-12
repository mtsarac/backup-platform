import { QueryState } from '@/components/data/query-state'
import { PageHeader } from '@/components/ui/page-header'
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
  const attempts = attemptsQuery.data ?? []
  return (
    <div className="space-y-6">
      <PageHeader
        crumbs={[{ label: tx('Paylaşımlarım') }]}
        title={tx('Paylaşımlarım')}
      />

      <QueryState isPending={query.isPending} error={query.error}>
        <SharesTable
          shares={shares}
          canAdmin={canAdmin}
          caption={tx('Paylaşımlarım')}
          emptyMessage={tx('Henüz paylaşım yapılmamış.')}
          emptyAction={
            <p className="text-sm text-text-muted">
              {tx('Yedek dosyası detayından PlatformAdmin için paylaşım oluşturabilirsiniz.')}
            </p>
          }
        />
      </QueryState>

      <Section
        title={tx('İndirme girişimleri')}
        description={tx('Çalışma alanınıza ait paylaşımlar ve yedek dosyaları üzerindeki indirme girişimleri.')}
      >
        <QueryState isPending={attemptsQuery.isPending} error={attemptsQuery.error}>
          <DownloadAttemptsTable attempts={attempts} />
        </QueryState>
      </Section>
    </div>
  )
}
