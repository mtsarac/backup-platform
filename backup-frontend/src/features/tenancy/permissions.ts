import type { TenantRole } from '@/features/auth/types'

export function tenantPermissions(role: TenantRole) {
  const canAdmin = role === 'Owner' || role === 'Admin' || role === 'Administrator'
  return { canAdmin, canOperate: canAdmin || role === 'Operator' }
}
