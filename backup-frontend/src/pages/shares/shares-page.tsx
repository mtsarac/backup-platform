import { QueryState } from '@/components/data/query-state'
import { Link } from 'react-router-dom'
import { buttonVariants } from '@/components/ui/button-variants'
import { DownloadAttemptsSection } from '@/features/shares/download-attempts-section'
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
      <p className="text-sm text-text-muted">{tx('Platform Yöneticisi\'nin bir yedeği süreli indirmesine verdiğiniz izinler. Genellikle destek isterken gerekir.')}</p>
      <QueryState isPending={query.isPending} error={query.error}>
        <SharesTable
          shares={shares}
          caption={tx('Paylaşımlarım')}
          emptyMessage={tx('Henüz paylaşım yok. Bir yedeğin ayrıntısından Platform Yöneticisi\'ne süreli indirme izni verebilirsiniz.')}
          emptyAction={<Link className={buttonVariants()} to={`/tenants/${tenantId}/backup-runs`}>{tx('Yedek dosyalarını aç')}</Link>}
        />
      </QueryState>

      <DownloadAttemptsSection scope={{ route: 'tenant', tenantId }} description={tx('Çalışma alanınıza ait paylaşımlar ve yedek dosyaları üzerindeki indirme girişimleri.')} />
    </div>
  )
}
