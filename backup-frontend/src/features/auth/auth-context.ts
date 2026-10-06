import { createContext } from 'react'
import type { TenantMembership, UserProfile } from './types'

export interface AuthContextValue {
  user: UserProfile | null
  tenants: TenantMembership[]
  isAuthenticated: boolean
  isLoading: boolean
  error: unknown
  sessionExpired: boolean
  login: (identifier: string, password: string) => Promise<void>
  logout: () => Promise<void>
  refresh: () => void
}
export const AuthContext = createContext<AuthContextValue | null>(null)
