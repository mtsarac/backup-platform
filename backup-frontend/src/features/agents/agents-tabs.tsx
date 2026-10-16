import { RouteTabs } from '@/components/ui/tabs'
import { useTenant } from '@/features/tenancy/use-tenant'

/** Shared by the Agents and Enrollment pages; Enrollment is administrator-only, so non-admins see no tabs. */
export function AgentsTabs() {
  const { tenantId, canAdmin } = useTenant()
  if (!canAdmin) return null
  return <RouteTabs label="Agent bölümleri" items={[
    { to: `/tenants/${tenantId}/agents`, label: "Agent'lar", end: true },
    { to: `/tenants/${tenantId}/enrollment`, label: 'Kayıt (Enrollment)' },
  ]} />
}
