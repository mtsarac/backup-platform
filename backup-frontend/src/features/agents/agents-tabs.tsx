import { tx, useLanguage } from '@/lib/i18n'
import { RouteTabs } from '@/components/ui/tabs'
import { useTenant } from '@/features/tenancy/use-tenant'

/** Shared by the computers and new-computer pages; enrolment is administrator-only, so non-admins see no tabs. */
export function AgentsTabs() {
  useLanguage()
  const { tenantId, canAdmin } = useTenant()
  if (!canAdmin) return null
  return <RouteTabs label={tx("Yedekleme bilgisayarı bölümleri")} items={[
    { to: `/tenants/${tenantId}/agents`, label: tx('Yedekleme bilgisayarları'), end: true },
    { to: `/tenants/${tenantId}/enrollment`, label: tx('Yeni bilgisayar kaydı') },
  ]} />
}
