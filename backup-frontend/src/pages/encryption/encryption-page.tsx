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
  const { tenantId, tenant, canAdmin } = useTenant()
  return <div className="space-y-6">
    <PageHeader title={tx("Şifreleme")} description={tx("Şifreli yedeklerinizin korumasını ve acil destek erişimi geçmişini inceleyin.")} />
    {canAdmin
      ? <EncryptionContent key={tenantId} tenantId={tenantId} tenantName={tenant.name} />
      : <AccessDenied what={tx("Şifreleme korumasını ve acil destek erişimi geçmişini yalnızca çalışma alanı sahibi veya yöneticisi görüntüleyebilir.")} />}
  </div>
}

function EncryptionContent({ tenantId, tenantName }: { tenantId: string; tenantName: string }) {
  useLanguage()
  const encryption = useTenantEncryption(tenantId)
  return <>
    <QueryState isPending={encryption.isPending} error={encryption.error}>
      {encryption.data && <TenantKeysSection tenantId={tenantId} tenantName={tenantName} encryption={encryption.data} />}
    </QueryState>
    <TenantBreakGlassSection tenantId={tenantId} />
  </>
}
