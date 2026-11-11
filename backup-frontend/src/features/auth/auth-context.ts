import { createContext } from 'react'
import type { TenantMembership, UserProfile } from './types'

export interface AuthContextValue {
  user: UserProfile | null
  tenants: TenantMembership[]
  isAuthenticated: boolean
  isLoading: boolean
  error: unknown
  sessionExpired: boolean
  login: (identifier: string, password: string, rememberMe: boolean) => Promise<{ secondFactorRequired?: boolean } | void>
  completeSecondFactor: () => Promise<void>
  logout: () => Promise<void>
  clearAuth: () => Promise<void>
  refresh: () => void
}
export const AuthContext = createContext<AuthContextValue | null>(null)
