import { createContext } from 'react'
import type { TenantMembership, UserProfile } from './types'

export type SecondFactorMethod = 'totp' | 'passkey' | 'recovery'

export interface LoginChallengeResult {
  secondFactorRequired?: boolean
  requiresSecondFactor?: boolean
  passwordChangeRequired?: boolean
  requiresPasswordChange?: boolean
  methods?: SecondFactorMethod[]
}

export interface AuthContextValue {
  user: UserProfile | null
  tenants: TenantMembership[]
  isAuthenticated: boolean
  isLoading: boolean
  error: unknown
  sessionExpired: boolean
  loggedOut?: boolean
  login: (identifier: string, password: string, rememberMe: boolean) => Promise<LoginChallengeResult | void>
  completeSecondFactor: () => Promise<void>
  establishSession?: () => Promise<void>
  loginWithPasskey?: (rememberMe: boolean, identifier?: string) => Promise<void>
  verifySecondFactor?: () => Promise<void>
  logout: () => Promise<void>
  clearAuth: () => Promise<void>
  refresh: () => void
}
export const AuthContext = createContext<AuthContextValue | null>(null)
