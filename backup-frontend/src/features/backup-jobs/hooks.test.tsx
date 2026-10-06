import type { ReactNode } from 'react'
import { act, renderHook } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { useRunBackupJob } from './hooks'
import { startJobRun } from './api'

const access = vi.hoisted(() => ({ tenantId: 'tenant-a', canOperate: true, canAdmin: false }))
vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => access }))
vi.mock('./api', () => ({ startJobRun: vi.fn(), getBackupJob: vi.fn(), getBackupJobs: vi.fn(), getOccurrences: vi.fn(), saveBackupJob: vi.fn(), setJobEnabled: vi.fn() }))
function wrapper({ children }: { children: ReactNode }) {
  return <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>{children}</QueryClientProvider>
}
beforeEach(() => { vi.clearAllMocks(); access.canOperate = true })
describe('manuel çalıştırma', () => {
  it('Viewer için API isteği göndermez', async () => {
    access.canOperate = false
    const { result } = renderHook(useRunBackupJob, { wrapper })
    await expect(result.current.run('job-a')).rejects.toThrow('yetkiniz yok')
    expect(startJobRun).not.toHaveBeenCalled()
  })
  it('eşzamanlı ikinci tıklamayı engeller', async () => {
    let resolve!: (value: { id: string }) => void
    vi.mocked(startJobRun).mockImplementation(() => new Promise(done => { resolve = done }))
    const { result } = renderHook(useRunBackupJob, { wrapper })
    await act(async () => {
      const first = result.current.run('job-a')
      await expect(result.current.run('job-a')).resolves.toBeUndefined()
      await Promise.resolve()
      resolve({ id: 'run-a' })
      await first
    })
    expect(startJobRun).toHaveBeenCalledTimes(1)
    expect(vi.mocked(startJobRun).mock.calls[0][2]).toMatch(/^[0-9a-f-]{36}$/i)
  })
  it('belirsiz ağ hatasından sonra aynı idempotency anahtarını kullanır', async () => {
    vi.mocked(startJobRun).mockRejectedValueOnce(new TypeError('Network failed')).mockResolvedValue({ id: 'run-a' })
    const { result } = renderHook(useRunBackupJob, { wrapper })
    await act(async () => { await expect(result.current.run('job-a')).rejects.toThrow('Network failed') })
    await act(async () => { await result.current.run('job-a') })
    const calls = vi.mocked(startJobRun).mock.calls
    expect(calls[0][2]).toBe(calls[1][2])
    await act(async () => { await result.current.run('job-a') })
    expect(vi.mocked(startJobRun).mock.calls[2][2]).not.toBe(calls[0][2])
  })
})
