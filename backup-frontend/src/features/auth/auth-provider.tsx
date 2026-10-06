import { useEffect, useState, type ReactNode } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { apiRequest, ApiError, resetAntiforgery, subscribeSessionExpired } from '@/lib/api/client'
import { AuthContext } from './auth-context'
import type { TenantMembership, UserProfile } from './types'

export function AuthProvider({ children }: { children: ReactNode }) {
  const cache = useQueryClient()
  const [sessionExpired, setSessionExpired] = useState(false)
  const me = useQuery({ queryKey: ['auth', 'me'], queryFn: async ({ signal }) => {
    try { return await apiRequest<UserProfile>('me', { signal }) }
    catch (error) { if (error instanceof ApiError && error.status === 401) return null; throw error }
  }, retry: false })
  const memberships = useQuery({ queryKey: ['auth', 'tenants'], queryFn: ({ signal }) => apiRequest<TenantMembership[]>('me/tenants', { signal }), enabled: !!me.data, retry: false })
  useEffect(() => subscribeSessionExpired(() => {
    if (cache.getQueryData(['auth', 'me'])) setSessionExpired(true)
    void cache.cancelQueries()
    cache.setQueryData(['auth', 'me'], null)
    cache.removeQueries({ queryKey: ['tenant'] })
    cache.removeQueries({ queryKey: ['auth', 'tenants'] })
  }), [cache])
  async function login(loginIdentifier: string, password: string) {
    await apiRequest<void>('auth/login', { method: 'POST', json: { loginIdentifier, password } })
    resetAntiforgery()
    await cache.cancelQueries()
    cache.removeQueries({ queryKey: ['tenant'] })
    const profile = await apiRequest<UserProfile>('me')
    const tenants = await apiRequest<TenantMembership[]>('me/tenants')
    setSessionExpired(false)
    cache.setQueryData(['auth', 'me'], profile)
    cache.setQueryData(['auth', 'tenants'], tenants)
  }
  async function logout() {
    await apiRequest<void>('auth/logout', { method: 'POST' })
    resetAntiforgery()
    await cache.cancelQueries()
    cache.removeQueries({ queryKey: ['tenant'] })
    cache.removeQueries({ queryKey: ['auth', 'tenants'] })
    cache.setQueryData(['auth', 'me'], null)
    setSessionExpired(false)
  }
  return <AuthContext.Provider value={{ user: me.data ?? null, tenants: memberships.data ?? [], isAuthenticated: !!me.data,
    isLoading: me.isPending || (!!me.data && memberships.isPending), error: me.error ?? memberships.error, sessionExpired,
    login, logout, refresh: () => { void me.refetch(); if (me.data) void memberships.refetch() } }}>{children}</AuthContext.Provider>
}
