import { tx, useLanguage } from '@/lib/i18n'
import { AccessDenied } from '@/components/auth/access-denied'
import { QueryState } from '@/components/data/query-state'
import { PageHeader } from '@/components/ui/page-header'
import { useTenantEncryption } from '@/features/artifact-encryption/hooks'
import { TenantBreakGlassSection } from '@/features/artifact-encryption/tenant-break-glass-section'
import { TenantKeysSection } from '@/features/artifact-encryption/tenant-recovery-key-section'
import { useTenant } from '@/features/tenancy/use-tenant'

export function EncryptionPage() {
  useLanguage()
  const { tenantId, canAdmin } = useTenant()
  if (!canAdmin) return <AccessDenied what={tx("Şifreleme anahtarlarını ve break-glass geçmişini yalnızca tenant Owner / Admin görüntüleyebilir.")} />
  return <EncryptionContent key={tenantId} tenantId={tenantId} />
}

function EncryptionContent({ tenantId }: { tenantId: string }) {
  useLanguage()
  const encryption = useTenantEncryption(tenantId)
  return <div className="space-y-6">
    <PageHeader title={tx("Şifreleme")} description={tx("Yedek şifreleme anahtarları, tenant kurtarma anahtarı ve platform yöneticilerinin break-glass erişimleri.")} />
    <QueryState isPending={encryption.isPending} error={encryption.error}>
      {encryption.data && <TenantKeysSection tenantId={tenantId} encryption={encryption.data} />}
    </QueryState>
    <TenantBreakGlassSection tenantId={tenantId} />
  </div>
}
