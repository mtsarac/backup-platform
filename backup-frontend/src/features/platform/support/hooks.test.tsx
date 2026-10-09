import { act, renderHook, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import type { ReactNode } from 'react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import * as api from './api'
import { makeArtifact, makeSession } from './test-fixtures'
import { removeSupportData, supportKeys, useSupportArtifact, useSupportSession } from './hooks'
import { useSupportCountdown } from './use-support-countdown'
import type { SupportScope } from './types'

vi.mock('./api')
beforeEach(() => vi.resetAllMocks())
afterEach(() => vi.useRealTimers())
function wrapper(client: QueryClient) {
  return function Provider({ children }: { children: ReactNode }) { return <QueryClientProvider client={client}>{children}</QueryClientProvider> }
}
describe('support clock and cache lifecycle', () => {
  it('uses server clock skew rather than client wall time and counts down to zero', () => {
    vi.useFakeTimers()
    vi.setSystemTime('2030-01-01T00:00:00Z')
    const received = Date.now()
    const { result } = renderHook(() => useSupportCountdown('2026-10-09T10:01:00Z', '2026-10-09T10:00:00Z', received))
    expect(result.current.seconds).toBe(60)
    act(() => vi.advanceTimersByTime(15_000))
    expect(result.current.seconds).toBe(45)
    act(() => vi.advanceTimersByTime(60_000))
    expect(result.current.seconds).toBe(0)
  })
  it('removes customer queries but retains audit/view and unrelated sessions; platform root removes everything on auth exit', () => {
    const client = new QueryClient()
    const scope = { sessionId: 'support-a', tenantId: 'tenant-a', enabled: true }
    for (const resource of ['jobs', 'runs', 'artifacts', 'downloads', 'restores']) client.setQueryData(supportKeys.data(scope, resource), ['private-data'])
    client.setQueryData(supportKeys.session('support-a'), makeSession())
    client.setQueryData(supportKeys.data(scope, 'audit'), ['audit'])
    const otherScope = { ...scope, sessionId: 'support-b', tenantId: 'tenant-b' }
    client.setQueryData(supportKeys.data(otherScope, 'artifacts'), ['other-data'])
    removeSupportData(client, 'support-a')
    for (const resource of ['jobs', 'runs', 'artifacts', 'downloads', 'restores']) expect(client.getQueryData(supportKeys.data(scope, resource))).toBeUndefined()
    expect(client.getQueryData(supportKeys.data(scope, 'audit'))).toEqual(['audit'])
    expect(client.getQueryData(supportKeys.session('support-a'))).toBeDefined()
    expect(client.getQueryData(supportKeys.data(otherScope, 'artifacts'))).toEqual(['other-data'])
    client.removeQueries({ queryKey: ['platform'] })
    expect(client.getQueryCache().getAll()).toHaveLength(0)
  })
  it('an effective Expired session response drops already cached customer metadata', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const scope = { sessionId: 'support-a', tenantId: 'tenant-a', enabled: true }
    client.setQueryData(supportKeys.data(scope, 'artifacts'), [makeArtifact()])
    vi.mocked(api.getSession).mockResolvedValue(makeSession({ status: 'Expired' }))
    const { result } = renderHook(() => useSupportSession('support-a'), { wrapper: wrapper(client) })
    await waitFor(() => expect(result.current.data?.status).toBe('Expired'))
    await waitFor(() => expect(client.getQueryData(supportKeys.data(scope, 'artifacts'))).toBeUndefined())
    expect(client.getQueryData(supportKeys.session('support-a'))).toBeDefined()
  })
  it('changing session AND tenant cannot show the previous artifact from a cached query', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const first: SupportScope = { sessionId: 'support-a', tenantId: 'tenant-a', enabled: true }
    const second: SupportScope = { sessionId: 'support-b', tenantId: 'tenant-b', enabled: false }
    client.setQueryData(supportKeys.data(first, 'artifacts', 'same-artifact-id'), makeArtifact({ databaseName: 'private tenant A' }))
    vi.mocked(api.getSupportArtifact).mockResolvedValue(makeArtifact({ databaseName: 'private tenant A' }))
    const { result, rerender } = renderHook(({ scope }) => useSupportArtifact(scope, 'same-artifact-id'), { initialProps: { scope: first }, wrapper: wrapper(client) })
    expect(result.current.data?.databaseName).toBe('private tenant A')
    rerender({ scope: second })
    expect(result.current.data).toBeUndefined()
    expect(client.getQueryCache().find({ queryKey: ['platform', 'support', 'session', 'support-b', 'tenant', 'tenant-b', 'artifacts', 'same-artifact-id'], exact: true })).toBeDefined()
    expect(vi.mocked(api.getSupportArtifact).mock.calls.every(call => call[0] === 'support-a')).toBe(true)
  })
})
