import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { createMemoryRouter, MemoryRouter, Route, RouterProvider, Routes } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '@/lib/api/client'
import { errorMessage } from '@/lib/api/failure-messages'
import { makeRun } from '@/features/backups/test-data'
import { runStatuses } from '@/features/backups/presentation'
import type { BackupRun } from '@/features/backups/types'
import { BackupDetailPage } from './backup-detail-page'

const state = vi.hoisted(() => ({
  canOperate: true,
  query: { data: undefined as BackupRun | undefined, isPending: false, error: null as unknown },
  retry: { isPending: false, error: null as unknown, mutateAsync: vi.fn(), reset: vi.fn() },
  cancel: { isPending: false, error: null as unknown, mutateAsync: vi.fn(), reset: vi.fn() },
}))
vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => ({ tenantId: 'tenant-a', canOperate: state.canOperate }) }))
vi.mock('@/features/backups/hooks', () => ({ useBackup: () => state.query, useBackupActions: () => ({ retry: state.retry, cancel: state.cancel }) }))
vi.mock('@/features/artifacts/artifact-details', () => ({ ArtifactDetails: () => <p>Yedek çıktısı ayrıntıları</p> }))

function renderDetail() {
  return render(<MemoryRouter initialEntries={['/tenants/tenant-a/backup-runs/run-id']}><Routes><Route path="/tenants/:tenantId/backup-runs/:id" element={<BackupDetailPage />} /></Routes></MemoryRouter>)
}

beforeEach(() => {
  vi.clearAllMocks()
  state.canOperate = true
  state.query.data = makeRun()
  state.query.error = null
  state.retry.error = null
  state.cancel.error = null
  state.retry.isPending = false
  state.cancel.isPending = false
})

describe('run detail controls and attention', () => {
  it('resets the selected tab and cancellation confirmation when navigating to another run', async () => {
    const router = createMemoryRouter([{ path: '/tenants/:tenantId/backup-runs/:id', element: <BackupDetailPage /> }], { initialEntries: ['/tenants/tenant-a/backup-runs/first'] })
    render(<RouterProvider router={router} />)
    fireEvent.click(screen.getByRole('tab', { name: 'Zamanlama ve politika' }))
    fireEvent.click(screen.getByRole('button', { name: 'İptal et' }))
    expect(await screen.findByRole('dialog')).toBeInTheDocument()
    await act(async () => { await router.navigate('/tenants/tenant-a/backup-runs/second') })
    expect(screen.getByRole('tab', { name: 'Özet' })).toHaveAttribute('aria-selected', 'true')
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(state.cancel.mutateAsync).not.toHaveBeenCalled()
  })

  it('uses cooperative cancellation response without prematurely claiming Cancelled', async () => {
    state.cancel.mutateAsync.mockResolvedValue(makeRun({ status: 'CancellationRequested' }))
    renderDetail()
    fireEvent.click(screen.getByRole('button', { name: 'İptal et' }))
    expect(state.cancel.mutateAsync).not.toHaveBeenCalled()
    fireEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'İptali onayla' }))
    await screen.findByText('İptal isteği iletildi. Ajanın işlemi durdurması bekleniyor.')
    expect(state.cancel.mutateAsync).toHaveBeenCalledTimes(1)
    expect(screen.queryByText('Çalıştırma iptal edildi.')).not.toBeInTheDocument()
    expect(screen.getAllByText('Çalışıyor').length).toBeGreaterThan(0)
  })

  it('drops the pending-cancellation notice once the agent has acknowledged and the run is Cancelled', async () => {
    state.cancel.mutateAsync.mockResolvedValue(makeRun({ status: 'CancellationRequested' }))
    const view = renderDetail()
    fireEvent.click(screen.getByRole('button', { name: 'İptal et' }))
    fireEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'İptali onayla' }))
    await screen.findByText('İptal isteği iletildi. Ajanın işlemi durdurması bekleniyor.')
    state.query.data = makeRun({ status: 'Cancelled' })
    view.rerender(<MemoryRouter initialEntries={['/tenants/tenant-a/backup-runs/run-id']}><Routes><Route path="/tenants/:tenantId/backup-runs/:id" element={<BackupDetailPage />} /></Routes></MemoryRouter>)
    expect(screen.queryByText('İptal isteği iletildi. Ajanın işlemi durdurması bekleniyor.')).not.toBeInTheDocument()
  })

  it('offers manual retry even for automatically nonretryable failures', async () => {
    const failed = makeRun({ status: 'Failed', reasonCode: 'PermissionDenied' })
    failed.retry.lastFailureRetryable = false
    state.query.data = failed
    state.retry.mutateAsync.mockResolvedValue(makeRun({ status: 'Pending' }))
    renderDetail()
    expect(screen.getByText(/Bu hata otomatik olarak yeniden denenmez/)).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Yeniden dene' }))
    await screen.findByText('Yeni deneme sıraya alındı.')
    expect(state.retry.mutateAsync).toHaveBeenCalledTimes(1)
  })

  it('uses centrally mapped 409 reason instead of raw backend errors', () => {
    state.query.data = makeRun({ status: 'Failed' })
    state.retry.error = new ApiError(409, { code: 'retry_not_allowed', reason: 'attempts_exhausted' })
    renderDetail()
    expect(screen.getByRole('alert')).toHaveTextContent(errorMessage(state.retry.error))
    expect(screen.queryByText('unsafe internal exception')).not.toBeInTheDocument()
  })

  it('hides retry/cancel for Viewer and disables duplicate mutations', () => {
    state.canOperate = false
    const view = renderDetail()
    expect(screen.queryByRole('button', { name: 'İptal et' })).not.toBeInTheDocument()
    state.canOperate = true
    state.cancel.isPending = true
    view.rerender(<MemoryRouter><BackupDetailPage /></MemoryRouter>)
    expect(screen.getByRole('button', { name: 'İletiliyor…' })).toBeDisabled()
  })

  it('shows progress including zero and unknown values, full attempt triggers, and no internal ids', () => {
    const run = makeRun()
    run.attempts[0].trigger = 'ManualRetry'
    run.attempts[0].progressPercent = 0
    state.query.data = run
    const view = renderDetail()
    fireEvent.click(screen.getByRole('tab', { name: /^Denemeler/ }))
    expect(screen.getByText('1. deneme · Elle yeniden deneme')).toBeInTheDocument()
    expect(screen.getByRole('progressbar')).toHaveAttribute('aria-valuenow', '0')
    expect(screen.getByText('Yedek oluşturuluyor')).toBeInTheDocument()
    expect(screen.queryByText('attempt-id')).not.toBeInTheDocument()
    expect(screen.queryByText('run-id')).not.toBeInTheDocument()
    run.attempts[0].progressPercent = null
    run.attempts[0].trigger = 'AutomaticRetry'
    view.rerender(<MemoryRouter><BackupDetailPage /></MemoryRouter>)
    fireEvent.click(screen.getByRole('tab', { name: /^Denemeler/ }))
    expect(screen.getByText('1. deneme · Otomatik yeniden deneme')).toBeInTheDocument()
    expect(screen.getByText('İlerleme: Yüzde bildirilmedi')).toBeInTheDocument()
    expect(screen.getByRole('progressbar')).not.toHaveAttribute('aria-valuenow')
  })

  it.each(runStatuses)('visibly distinguishes %s with eligible controls', async (status) => {
    state.query.data = makeRun({ status })
    renderDetail()
    const label = { Pending: 'Sırada bekliyor', Leased: 'Agent görevi aldı', Running: 'Çalışıyor', RetryPending: 'Yeniden denenecek', CancellationRequested: 'İptal isteniyor', Succeeded: 'Başarılı', Failed: 'Başarısız', Cancelled: 'İptal edildi', AgentLost: '' }[status]
    if (label) expect(screen.getAllByText(label).length).toBeGreaterThan(0)
    if (['Succeeded', 'Cancelled', 'CancellationRequested'].includes(status)) {
      expect(screen.queryByRole('button', { name: 'Yeniden dene' })).not.toBeInTheDocument()
      expect(screen.queryByRole('button', { name: 'İptal et' })).not.toBeInTheDocument()
    }
    if (status === 'RetryPending') expect(screen.getByText(/Otomatik yeniden deneme bekleniyor/)).toBeInTheDocument()
    if (status === 'CancellationRequested') expect(screen.getByText(/henüz iptal edilmiş değildir/)).toBeInTheDocument()
    await waitFor(() => expect(screen.getByText('Çalıştırma özeti')).toBeInTheDocument())
  })
})
