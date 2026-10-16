import { tx, useLanguage } from '@/lib/i18n'
import { RouteTabs } from '@/components/ui/tabs'
import { useTenant } from '@/features/tenancy/use-tenant'

/** Shared by the Agents and Enrollment pages; Enrollment is administrator-only, so non-admins see no tabs. */
export function AgentsTabs() {
  useLanguage()
  const { tenantId, canAdmin } = useTenant()
  if (!canAdmin) return null
  return <RouteTabs label={tx("Agent bölümleri")} items={[
    { to: `/tenants/${tenantId}/agents`, label: tx("Agent'lar"), end: true },
    { to: `/tenants/${tenantId}/enrollment`, label: tx('Kayıt (Enrollment)') },
  ]} />
}
