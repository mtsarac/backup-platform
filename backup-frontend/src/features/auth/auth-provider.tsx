import { useEffect, useState, type ReactNode } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { apiRequest, ApiError, resetAntiforgery, subscribeSessionExpired } from '@/lib/api/client'
import { AuthContext } from './auth-context'
import type { TenantMembership, UserProfile } from './types'
import { getAssertion } from './webauthn'

export function AuthProvider({ children }: { children: ReactNode }) {
  const cache = useQueryClient()
  const [sessionExpired, setSessionExpired] = useState(false)
  const me = useQuery({ queryKey: ['auth', 'me'], queryFn: async ({ signal }) => {
    try { return await apiRequest<UserProfile>('me', { signal }) }
    catch (error) { if (error instanceof ApiError && error.status === 401) return null; throw error }
  }, retry: false })
  const memberships = useQuery({ queryKey: ['auth', 'tenants'], queryFn: ({ signal }) => apiRequest<TenantMembership[]>('me/tenants', { signal }), enabled: !!me.data, staleTime: 1_000, retry: false })
  useEffect(() => subscribeSessionExpired(() => {
    if (cache.getQueryData(['auth', 'me'])) setSessionExpired(true)
    void cache.cancelQueries()
    cache.setQueryData(['auth', 'me'], null)
    cache.removeQueries({ queryKey: ['tenant'] })
    cache.removeQueries({ queryKey: ['platform'] })
    cache.removeQueries({ queryKey: ['auth', 'tenants'] })
    cache.removeQueries({ queryKey: ['auth', 'sessions'] })
    cache.removeQueries({ queryKey: ['auth', 'security'] })
    cache.removeQueries({ queryKey: ['auth', 'passkeys'] })
  }), [cache])
  async function completeSecondFactor() {
    resetAntiforgery()
    await cache.cancelQueries()
    cache.removeQueries({ queryKey: ['tenant'] })
    cache.removeQueries({ queryKey: ['platform'] })
    cache.removeQueries({ queryKey: ['auth', 'sessions'] })
    cache.removeQueries({ queryKey: ['auth', 'security'] })
    cache.removeQueries({ queryKey: ['auth', 'passkeys'] })
    const profile = await apiRequest<UserProfile>('me')
    const tenants = await apiRequest<TenantMembership[]>('me/tenants')
    setSessionExpired(false)
    cache.setQueryData(['auth', 'me'], profile)
    cache.setQueryData(['auth', 'tenants'], tenants)
  }
  const establishSession = completeSecondFactor
  async function login(loginIdentifier: string, password: string, rememberMe: boolean) {
    const result = await apiRequest<{ secondFactorRequired?: boolean; requiresSecondFactor?: boolean } | void>('auth/login', { method: 'POST', json: { loginIdentifier, password, rememberMe } })
    if (result && typeof result === 'object' && (result.secondFactorRequired || result.requiresSecondFactor)) {
      return { secondFactorRequired: true, requiresSecondFactor: true }
    }
    await completeSecondFactor()
    return { secondFactorRequired: false, requiresSecondFactor: false }
  }
  async function loginWithPasskey(rememberMe: boolean, loginIdentifier?: string) {
    const { options, challengeId } = await apiRequest<{ options: Parameters<typeof getAssertion>[0]; challengeId: string }>('auth/passkeys/login/options', {
      method: 'POST',
      json: { loginIdentifier: loginIdentifier || undefined }
    })
    const assertion = await getAssertion(options)
    await apiRequest<void>('auth/passkeys/login', {
      method: 'POST',
      json: { challengeId, response: assertion, rememberMe }
    })
    await establishSession()
  }
  async function verifySecondFactor() {
    const { options, challengeId } = await apiRequest<{ options: Parameters<typeof getAssertion>[0]; challengeId: string }>('auth/passkeys/second-factor/options', {
      method: 'POST'
    })
    const assertion = await getAssertion(options)
    await apiRequest<void>('auth/passkeys/second-factor', {
      method: 'POST',
      json: { challengeId, response: assertion }
    })
    await establishSession()
  }
  async function clearAuth() {
    resetAntiforgery()
    await cache.cancelQueries()
    cache.removeQueries({ queryKey: ['tenant'] })
    cache.removeQueries({ queryKey: ['platform'] })
    cache.removeQueries({ queryKey: ['auth', 'tenants'] })
    cache.removeQueries({ queryKey: ['auth', 'sessions'] })
    cache.removeQueries({ queryKey: ['auth', 'security'] })
    cache.removeQueries({ queryKey: ['auth', 'passkeys'] })
    cache.setQueryData(['auth', 'me'], null)
    setSessionExpired(false)
  }
  async function logout() {
    await apiRequest<void>('auth/logout', { method: 'POST' })
    await clearAuth()
  }
  return <AuthContext.Provider value={{ user: me.data ?? null, tenants: memberships.data ?? [], isAuthenticated: !!me.data,
    isLoading: me.isPending || (!!me.data && memberships.isPending), error: me.error ?? memberships.error, sessionExpired,
    login, completeSecondFactor, establishSession, loginWithPasskey, verifySecondFactor, logout, clearAuth, refresh: () => { void me.refetch(); if (me.data) void memberships.refetch() } }}>{children}</AuthContext.Provider>
}
