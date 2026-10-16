import { tx } from '@/lib/i18n'
import { render } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import type { ReactNode } from 'react'
import { vi } from 'vitest'
import { AuthContext } from '@/features/auth/auth-context'
import type { TenantMembership, UserProfile } from '@/features/auth/types'
import { LocationProbe } from './location-probe'
import type { PagedResult, PlatformAgent } from './types'

export const platformAdmin: UserProfile = { userId: 'admin-id', get displayName() { return tx("Platform Operatörü") }, isPlatformAdmin: true }
export const tenantOwner: UserProfile = { userId: 'owner-id', get displayName() { return tx("Tenant Sahibi") }, isPlatformAdmin: false }

export function paged<T>(items: T[], overrides: Partial<PagedResult<T>> = {}): PagedResult<T> {
  return { items, totalCount: items.length, page: 1, pageSize: 25, ...overrides }
}

export function makePlatformAgent(overrides: Partial<PlatformAgent> = {}): PlatformAgent {
  return {
    tenantId: 'tenant-a', tenantName: tx('Acme A.Ş.'), agentId: 'agent-a', hostName: 'SQL-01', status: 'Active', connectivity: 'online',
    operationalStatus: 'Online', platform: 'Windows', osDescription: 'Windows Server 2025', agentVersion: '1.4.0', protocolVersion: '1.0',
    capabilityCount: 3, capabilityRevision: 'rev-3', capabilitiesReportedAt: '2026-10-06T10:00:00Z', lastSeenAt: '2026-10-06T12:00:00Z', enrolledAt: '2026-10-01T10:00:00Z',
    ...overrides,
  }
}

/** `ui` is the element at `route`; pass `routes` instead to mount a full route tree (layout/guard routes). */
export function renderPlatform(ui: ReactNode, { path = '/platform', route = '*', user = platformAdmin, routes, tenants = [] }: { path?: string; route?: string; user?: UserProfile | null; routes?: ReactNode; tenants?: TenantMembership[] } = {}) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  const auth = { user, tenants, isAuthenticated: Boolean(user), isLoading: false, error: null, sessionExpired: false, login: vi.fn(), logout: vi.fn(), refresh: vi.fn() }
  const view = render(<QueryClientProvider client={client}><AuthContext.Provider value={auth}><MemoryRouter initialEntries={[path]}><Routes>{routes ?? <Route path={route} element={ui} />}</Routes><LocationProbe /></MemoryRouter></AuthContext.Provider></QueryClientProvider>)
  return { client, auth, ...view }
}
