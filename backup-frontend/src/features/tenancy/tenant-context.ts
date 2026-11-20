import { createContext } from 'react'
import type { TenantMembership } from '@/features/auth/types'

export interface TenantContextValue { tenantId: string; tenant: TenantMembership; switchTenant: (id: string) => Promise<void> }
export const TenantContext = createContext<TenantContextValue | null>(null)
