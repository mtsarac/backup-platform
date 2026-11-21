import { tx, useLanguage } from '@/lib/i18n'
import { QueryState } from '@/components/data/query-state'
import { useTenantEncryption } from '@/features/artifact-encryption/hooks'
import { TenantKeysSection } from '@/features/artifact-encryption/tenant-recovery-key-section'
import { useTenant } from '@/features/tenancy/use-tenant'

export function EncryptionPage() {
  useLanguage()
  const { tenantId, tenant } = useTenant()
  return <div className="space-y-6">
    <p className="text-body text-text-muted">{tx("Şifreli yedeklerinizin korumasını inceleyin.")}</p>
    <EncryptionContent key={tenantId} tenantId={tenantId} tenantName={tenant.name} />
  </div>
}

function EncryptionContent({ tenantId, tenantName }: { tenantId: string; tenantName: string }) {
  useLanguage()
  const encryption = useTenantEncryption(tenantId)
  return <>
    <QueryState isPending={encryption.isPending} error={encryption.error}>
      {encryption.data && <TenantKeysSection tenantId={tenantId} tenantName={tenantName} encryption={encryption.data} />}
    </QueryState>
  </>
}
