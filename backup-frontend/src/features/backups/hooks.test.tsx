import type { ReactNode } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { act, renderHook } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cancelBackup, getBackup, retryBackup } from './api'
import { backupQueryKeys, useBackup, useBackupActions } from './hooks'
import { makeRun } from './test-data'

vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => ({ tenantId: 'tenant-a' }) }))
vi.mock('./api', () => ({ getBackup: vi.fn(), getBackups: vi.fn(), retryBackup: vi.fn(), cancelBackup: vi.fn() }))

const clients: QueryClient[] = []
function harness() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: Infinity }, mutations: { retry: false } } })
  clients.push(client)
  return { client, wrapper: ({ children }: { children: ReactNode }) => <QueryClientProvider client={client}>{children}</QueryClientProvider> }
}

beforeEach(() => {
  vi.clearAllMocks()
  Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'visible' })
})
afterEach(() => { clients.splice(0).forEach((client) => client.clear()); vi.useRealTimers() })

describe('tenant run lifecycle', () => {

  it('uses cancellation response state and invalidates only related tenant resources', async () => {
    const { wrapper, client } = harness()
    client.setQueryData(backupQueryKeys.detail('tenant-a', 'run-id'), makeRun())
    client.setQueryData(['tenant', 'tenant-a', 'backup-jobs'], ['unchanged'])
    client.setQueryData(['tenant', 'tenant-b', 'backup-runs'], ['other tenant'])
    const cancelled = makeRun({ status: 'CancellationRequested' })
    vi.mocked(cancelBackup).mockResolvedValue(cancelled)
    const invalidate = vi.spyOn(client, 'invalidateQueries')
    const hook = renderHook(() => useBackupActions('run-id'), { wrapper })
    await act(async () => { await hook.result.current.cancel.mutateAsync() })
    expect(client.getQueryData(backupQueryKeys.detail('tenant-a', 'run-id'))).toEqual(cancelled)
    expect(invalidate.mock.calls.map(([options]) => options?.queryKey)).toEqual([
      ['tenant', 'tenant-a', 'backup-runs'], ['tenant', 'tenant-a', 'dashboard'], ['tenant', 'tenant-a', 'backup-artifacts'],
    ])
    expect(client.getQueryState(['tenant', 'tenant-a', 'backup-jobs'])?.isInvalidated).toBe(false)
    expect(client.getQueryState(['tenant', 'tenant-b', 'backup-runs'])?.isInvalidated).toBe(false)
  })

  it('reopens Failed only from server response, and does not retry failed POSTs', async () => {
    const { wrapper, client } = harness()
    const failed = makeRun({ status: 'Failed' })
    client.setQueryData(backupQueryKeys.detail('tenant-a', 'run-id'), failed)
    vi.mocked(retryBackup).mockRejectedValueOnce(new Error('conflict'))
    const hook = renderHook(() => useBackupActions('run-id'), { wrapper })
    await act(async () => { await expect(hook.result.current.retry.mutateAsync()).rejects.toThrow('conflict') })
    expect(retryBackup).toHaveBeenCalledTimes(1)
    expect(client.getQueryData(backupQueryKeys.detail('tenant-a', 'run-id'))).toEqual(failed)
    const queued = makeRun({ status: 'Pending', currentAttemptNumber: 2 })
    vi.mocked(retryBackup).mockResolvedValueOnce(queued)
    await act(async () => { await hook.result.current.retry.mutateAsync() })
    expect(client.getQueryData(backupQueryKeys.detail('tenant-a', 'run-id'))).toEqual(queued)
  })

  it('polls only active runs in a visible tab and stops when terminal', async () => {
    vi.useFakeTimers()
    const run = makeRun()
    vi.mocked(getBackup).mockResolvedValue(run)
    const { wrapper, client } = harness()
    const hook = renderHook(() => useBackup('run-id'), { wrapper })
    await act(async () => { await vi.advanceTimersByTimeAsync(1) })
    expect(hook.result.current.isSuccess).toBe(true)
    await act(async () => { await vi.advanceTimersByTimeAsync(5000) })
    expect(getBackup).toHaveBeenCalledTimes(2)
    Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'hidden' })
    act(() => document.dispatchEvent(new Event('visibilitychange')))
    await act(async () => { await vi.advanceTimersByTimeAsync(10000) })
    expect(getBackup).toHaveBeenCalledTimes(2)
    vi.mocked(getBackup).mockResolvedValue(makeRun({ status: 'Succeeded' }))
    await act(async () => { client.setQueryData(backupQueryKeys.detail('tenant-a', 'run-id'), makeRun({ status: 'Succeeded' })); await vi.advanceTimersByTimeAsync(0) })
    Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'visible' })
    act(() => document.dispatchEvent(new Event('visibilitychange')))
    await act(async () => { await vi.advanceTimersByTimeAsync(10000) })
    expect(getBackup).toHaveBeenCalledTimes(2)
  })
})
