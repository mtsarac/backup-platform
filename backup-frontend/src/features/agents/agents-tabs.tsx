import { tx, useLanguage } from '@/lib/i18n'
import { RouteTabs } from '@/components/ui/tabs'
import { useTenant } from '@/features/tenancy/use-tenant'

/** Shared by the computers, discovered-databases and new-computer pages; enrolment is administrator-only, so non-admins do not see its tab. */
export function AgentsTabs() {
  useLanguage()
  const { tenantId, canAdmin } = useTenant()
  return <RouteTabs label={tx("Bilgisayar bölümleri")} items={[
    { to: `/tenants/${tenantId}/agents`, label: tx('Bilgisayarlar'), end: true },
    { to: `/tenants/${tenantId}/agents/databases`, label: tx('Bulunan veritabanları') },
    ...(canAdmin ? [{ to: `/tenants/${tenantId}/enrollment`, label: tx('Yeni bilgisayar kaydı') }] : []),
  ]} />
}
