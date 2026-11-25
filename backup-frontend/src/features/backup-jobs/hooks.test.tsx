import type { ReactNode } from 'react'
import { act, renderHook } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { useRunBackupJob, useSaveBackupJob } from './hooks'
import { saveBackupJob, startJobRun } from './api'
import { pendingKeys } from '@/lib/idempotency-key'

const access = vi.hoisted(() => ({ tenantId: 'tenant-a' }))
vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => access }))
vi.mock('./api', () => ({ startJobRun: vi.fn(), getBackupJob: vi.fn(), getBackupJobs: vi.fn(), getOccurrences: vi.fn(), saveBackupJob: vi.fn(), setJobEnabled: vi.fn() }))
function wrapper({ children }: { children: ReactNode }) {
  return <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>{children}</QueryClientProvider>
}
beforeEach(() => { vi.clearAllMocks() })
describe('manuel çalıştırma', () => {
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
describe('job oluşturma', () => {
  const input = { name: 'Gece' } as never
  it('sayfa yeniden yüklendikten sonra aynı istek aynı anahtarı kullanır, baska kiracı veya cıkıs kullanmaz', async () => {
    vi.mocked(saveBackupJob).mockRejectedValueOnce(new TypeError('response lost')).mockResolvedValue({ id: 'job-a' } as never)
    const first = renderHook(() => useSaveBackupJob(), { wrapper })
    await act(async () => { await expect(first.result.current.mutateAsync(input)).rejects.toThrow('response lost') })
    first.unmount()
    const reloaded = renderHook(() => useSaveBackupJob(), { wrapper })
    await act(async () => { await reloaded.result.current.mutateAsync(input) })
    const calls = vi.mocked(saveBackupJob).mock.calls
    expect(calls[1][3]).toBe(calls[0][3])
    expect(sessionStorage.length).toBe(0)

    vi.mocked(saveBackupJob).mockRejectedValueOnce(new TypeError('response lost'))
    await act(async () => { await expect(reloaded.result.current.mutateAsync(input)).rejects.toThrow('response lost') })
    access.tenantId = 'tenant-b'
    const other = renderHook(() => useSaveBackupJob(), { wrapper })
    await act(async () => { await other.result.current.mutateAsync(input) })
    access.tenantId = 'tenant-a'
    expect(vi.mocked(saveBackupJob).mock.calls[3][3]).not.toBe(vi.mocked(saveBackupJob).mock.calls[2][3])
    pendingKeys.clear()
    const afterLogout = renderHook(() => useSaveBackupJob(), { wrapper })
    await act(async () => { await afterLogout.result.current.mutateAsync(input) })
    expect(vi.mocked(saveBackupJob).mock.calls[4][3]).not.toBe(vi.mocked(saveBackupJob).mock.calls[2][3])
    expect(sessionStorage.length).toBe(0)
  })
  it('belirsiz hatadan sonra aynı anahtarla yeniden dener, başarıdan sonra yeni anahtar kullanır', async () => {
    vi.mocked(saveBackupJob).mockRejectedValueOnce(new TypeError('Network failed')).mockResolvedValue({ id: 'job-a' } as never)
    const { result } = renderHook(() => useSaveBackupJob(), { wrapper })
    await act(async () => { await expect(result.current.mutateAsync(input)).rejects.toThrow('Network failed') })
    await act(async () => { await result.current.mutateAsync(input) })
    const calls = vi.mocked(saveBackupJob).mock.calls
    expect(calls[0][3]).toMatch(/^[0-9a-f-]{36}$/i)
    expect(calls[1][3]).toBe(calls[0][3])
    await act(async () => { await result.current.mutateAsync(input) })
    expect(vi.mocked(saveBackupJob).mock.calls[2][3]).not.toBe(calls[0][3])
  })
  it('farklı iş tamamlandığında belirsiz kalan işin anahtarını korur', async () => {
    const a = { name: 'A' } as never
    const b = { name: 'B' } as never
    vi.mocked(saveBackupJob).mockRejectedValueOnce(new TypeError('response lost')).mockResolvedValue({ id: 'job' } as never)
    const { result } = renderHook(() => useSaveBackupJob(), { wrapper })
    await act(async () => { await expect(result.current.mutateAsync(a)).rejects.toThrow('response lost') })
    await act(async () => { await result.current.mutateAsync(b) })
    await act(async () => { await result.current.mutateAsync(a) })
    const calls = vi.mocked(saveBackupJob).mock.calls
    expect(calls[0][3]).not.toBe(calls[1][3])
    expect(calls[2][3]).toBe(calls[0][3])
  })
  it('eşzamanlı farklı işlerden biri bitince diğerinin anahtarını silmez', async () => {
    const firstCall = Promise.withResolvers<never>()
    const secondCall = Promise.withResolvers<never>()
    vi.mocked(saveBackupJob)
      .mockReturnValueOnce(firstCall.promise)
      .mockReturnValueOnce(secondCall.promise)
      .mockResolvedValue({ id: 'job-a' } as never)
    const { result } = renderHook(() => useSaveBackupJob(), { wrapper })
    const a = { name: 'A' } as never
    const b = { name: 'B' } as never
    await act(async () => {
      const first = result.current.mutateAsync(a).catch(() => undefined)
      const second = result.current.mutateAsync(b)
      await Promise.resolve()
      secondCall.resolve({ id: 'job-b' } as never)
      await second
      firstCall.reject(new TypeError('response lost'))
      await first
    })
    await act(async () => { await result.current.mutateAsync(a) })
    const calls = vi.mocked(saveBackupJob).mock.calls
    expect(calls[0][3]).not.toBe(calls[1][3])
    expect(calls[2][3]).toBe(calls[0][3])
  })
  it('aynı işin eşzamanlı yanıtlarından biri gelince diğeri sürerken anahtarı korur', async () => {
    const firstCall = Promise.withResolvers<never>()
    const secondCall = Promise.withResolvers<never>()
    vi.mocked(saveBackupJob)
      .mockReturnValueOnce(firstCall.promise)
      .mockReturnValueOnce(secondCall.promise)
      .mockResolvedValue({ id: 'job-a' } as never)
    const { result } = renderHook(() => useSaveBackupJob(), { wrapper })
    await act(async () => {
      const first = result.current.mutateAsync(input)
      const second = result.current.mutateAsync(input).catch(() => undefined)
      await Promise.resolve()
      firstCall.resolve({ id: 'job-a' } as never)
      await first
      await result.current.mutateAsync(input)
      secondCall.reject(new TypeError('response lost'))
      await second
    })
    const calls = vi.mocked(saveBackupJob).mock.calls
    expect(calls[0][3]).toBe(calls[1][3])
    expect(calls[2][3]).toBe(calls[0][3])
    await act(async () => { await result.current.mutateAsync(input) })
    expect(vi.mocked(saveBackupJob).mock.calls[3][3]).not.toBe(calls[0][3])
  })
  it('düzenlemede (PUT) anahtar göndermez', async () => {
    vi.mocked(saveBackupJob).mockResolvedValue({ id: 'job-a' } as never)
    const { result } = renderHook(() => useSaveBackupJob('job-a'), { wrapper })
    await act(async () => { await result.current.mutateAsync(input) })
    expect(vi.mocked(saveBackupJob).mock.calls[0][3]).toBeUndefined()
  })
})
