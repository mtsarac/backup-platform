import { createContext } from 'react'
import type { TenantMembership } from '@/features/auth/types'

export interface TenantContextValue { tenantId: string; tenant: TenantMembership; canAdmin: boolean; canOperate: boolean; switchTenant: (id: string) => Promise<void> }
export const TenantContext = createContext<TenantContextValue | null>(null)
