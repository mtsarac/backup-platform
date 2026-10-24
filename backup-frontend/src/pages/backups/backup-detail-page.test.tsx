import { act, fireEvent, screen, waitFor, within } from '@testing-library/react'
import { render } from '@/test/render'
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
  pause: { isPending: false, error: null as unknown, mutateAsync: vi.fn(), reset: vi.fn() },
  resume: { isPending: false, error: null as unknown, mutateAsync: vi.fn(), reset: vi.fn() },
}))
vi.mock('@tanstack/react-query', async importOriginal => ({ ...await importOriginal<typeof import('@tanstack/react-query')>(), useQueryClient: () => ({ refetchQueries: vi.fn() }) }))
vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => ({ tenantId: 'tenant-a', canOperate: state.canOperate }) }))
vi.mock('@/features/backups/hooks', () => ({ useBackup: () => state.query, useBackupActions: () => ({ retry: state.retry, cancel: state.cancel, pause: state.pause, resume: state.resume }) }))
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
  state.retry.isPending = false
  state.cancel.isPending = false
  state.pause.isPending = false
  state.resume.isPending = false
  state.pause.error = null
  state.resume.error = null
})

describe('run detail controls and attention', () => {
  it('shows a yellow advisory and historical comparison without treating a successful backup as failed', () => {
    state.query.data = makeRun({ status: 'Succeeded', isSizeAnomaly: true, sizeAnomalyStatus: 'SizeAnomalyWarning', sizeAnomalyBaselineBytes: 5_000_000_000, sizeAnomalyDeviationRatio: -0.5 })
    renderDetail()
    expect(screen.getByText('Boyut Anomalisi')).toBeInTheDocument()
    expect(screen.getByText('Boyut Anomalisi')).toHaveClass('bg-warning-subtle')
    expect(screen.getByText(/Geçmiş ortalama: 5 GB/)).toHaveTextContent('-%50')
    expect(screen.getByText('Yedek başarıyla tamamlandı. Boyut uyarısı yedeği geçersiz kılmaz; beklenmeyen veri değişikliklerini inceleyin.')).toBeInTheDocument()
    expect(screen.getByText('Başarılı')).toBeInTheDocument()
    expect(screen.queryByText('Çalıştırma başarısız oldu')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Yeniden dene' })).not.toBeInTheDocument()
  })

  it('distinguishes cold-start metadata from missing measurements', () => {
    state.query.data = makeRun({ status: 'Succeeded', sizeAnomalyStatus: 'BaselinePending' })
    renderDetail()
    expect(screen.getByText('Boyut baz çizgisi bekleniyor')).toBeInTheDocument()
    expect(screen.getByText('Karşılaştırma için en az 3 önceki başarılı yedek gerekir.')).toBeInTheDocument()
    expect(screen.queryByText('Boyut Anomalisi')).not.toBeInTheDocument()
    expect(screen.queryByText(/Geçmiş ortalama:/)).not.toBeInTheDocument()
  })

  it('resets the selected tab and cancellation confirmation when navigating to another run', async () => {
    const router = createMemoryRouter([{ path: '/tenants/:tenantId/backup-runs/:id', element: <BackupDetailPage /> }], { initialEntries: ['/tenants/tenant-a/backup-runs/first'] })
    render(<RouterProvider router={router} />)
    fireEvent.click(screen.getByRole('tab', { name: /^Denemeler/ }))
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
    await screen.findByText('İptal isteği iletildi. Yedekleme bilgisayarının işlemi durdurması bekleniyor.')
    expect(state.cancel.mutateAsync).toHaveBeenCalledTimes(1)
    expect(screen.queryByText('Yedekleme denemesi iptal edildi.')).not.toBeInTheDocument()
    expect(screen.getAllByText('Çalışıyor').length).toBeGreaterThan(0)
  })

  it('drops the pending-cancellation notice once the agent has acknowledged and the run is Cancelled', async () => {
    state.cancel.mutateAsync.mockResolvedValue(makeRun({ status: 'CancellationRequested' }))
    const view = renderDetail()
    fireEvent.click(screen.getByRole('button', { name: 'İptal et' }))
    fireEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'İptali onayla' }))
    await screen.findByText('İptal isteği iletildi. Yedekleme bilgisayarının işlemi durdurması bekleniyor.')
    state.query.data = makeRun({ status: 'Cancelled' })
    view.rerender(<MemoryRouter initialEntries={['/tenants/tenant-a/backup-runs/run-id']}><Routes><Route path="/tenants/:tenantId/backup-runs/:id" element={<BackupDetailPage />} /></Routes></MemoryRouter>)
    expect(screen.queryByText('İptal isteği iletildi. Yedekleme bilgisayarının işlemi durdurması bekleniyor.')).not.toBeInTheDocument()
  })

  it('offers manual retry even for automatically nonretryable failures', async () => {
    const failed = makeRun({ status: 'Failed', reasonCode: 'PermissionDenied' })
    failed.retry.lastFailureRetryable = false
    state.query.data = failed
    state.retry.mutateAsync.mockResolvedValue(makeRun({ status: 'Pending' }))
    renderDetail()
    expect(within(screen.getByRole('alert')).getByText(/Yeniden dene/)).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Yeniden dene' }))
    await screen.findByText('Yeni deneme sıraya alındı.')
    expect(state.retry.mutateAsync).toHaveBeenCalledTimes(1)
  })

  it('uses centrally mapped 409 reason instead of raw backend errors', () => {
    state.query.data = makeRun({ status: 'Failed' })
    state.retry.error = new ApiError(409, { code: 'retry_not_allowed', reason: 'attempts_exhausted' })
    renderDetail()
    expect(screen.getAllByRole('alert').map((alert) => alert.textContent).join(' ')).toContain(errorMessage(state.retry.error))
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
    const label = { Pending: 'Sırada bekliyor', Leased: /(?:Agent görevi aldı|Ajan görevi aldı)/, Running: 'Çalışıyor', RetryPending: 'Yeniden denenecek', CancellationRequested: 'İptal isteniyor', Succeeded: 'Başarılı', Failed: 'Başarısız', Cancelled: 'İptal edildi', AgentLost: '', Paused: 'Duraklatıldı' }[status]
    if (label) expect(screen.getAllByText(label).length).toBeGreaterThan(0)
    if (['Succeeded', 'Cancelled', 'CancellationRequested'].includes(status)) {
      expect(screen.queryByRole('button', { name: 'Yeniden dene' })).not.toBeInTheDocument()
      expect(screen.queryByRole('button', { name: 'İptal et' })).not.toBeInTheDocument()
    }
    if (status === 'RetryPending') expect(screen.getByText(/Otomatik yeniden deneme bekleniyor/)).toBeInTheDocument()
    if (status === 'CancellationRequested') expect(screen.getByText(/henüz iptal edilmiş değildir/)).toBeInTheDocument()
    await waitFor(() => expect(screen.getByText('Yedekleme denemesi özeti')).toBeInTheDocument())
  })

  it('says what went wrong, that data is safe and what to do, with the code only under collapsed technical details', () => {
    state.query.data = makeRun({ status: 'Failed', reasonCode: 'PermissionDenied' })
    renderDetail()
    const alert = within(screen.getByRole('alert'))
    expect(alert.getByText('İşlem için gerekli izinler bulunmuyor.')).toBeInTheDocument()
    expect(alert.getByText(/Verileriniz için:/)).toBeInTheDocument()
    expect(alert.getByText(/Ne yapmalı:/).closest('p')).toHaveTextContent(/Yeniden dene/)
    const code = alert.getByText('PermissionDenied')
    expect(code.closest('details')).not.toHaveAttribute('open')
    expect(screen.getByRole('button', { name: 'Yeniden dene' })).toBeEnabled()
  })

  it('sends a viewer to their administrator instead of offering retry', () => {
    state.canOperate = false
    state.query.data = makeRun({ status: 'AgentLost' })
    renderDetail()
    expect(within(screen.getByRole('alert')).getByText(/Ne yapmalı:/).closest('p')).toHaveTextContent('Yöneticinize danışın')
    expect(screen.queryByRole('button', { name: 'Yeniden dene' })).not.toBeInTheDocument()
  })

  it('keeps trigger, planned time, attempt counts, engine and the run id under collapsed copyable technical details', () => {
    state.query.data = makeRun({ scheduledFor: '2026-10-06T09:59:00Z' })
    renderDetail()
    const details = screen.getByRole('region', { name: 'Yedekleme denemesi ayrıntıları' })
    expect(within(details).getByText('Teknik ayrıntılar').closest('details')).not.toHaveAttribute('open')
    for (const label of ['Yedekleme denemesi kimliği', 'Tetikleyici', 'Planlanan zaman', 'Deneme sayısı', 'Veritabanı motoru']) expect(within(details).getByText(label)).toBeInTheDocument()
    expect(within(details).getByText('run-id')).toBeInTheDocument()
    expect(within(details).getByRole('button', { name: 'Kopyala: Yedekleme denemesi kimliği' })).toBeInTheDocument()
    expect(screen.queryByRole('tab', { name: 'Zamanlama ve politika' })).not.toBeInTheDocument()
    for (const shown of screen.getAllByText('Elle başlatıldı')) expect(shown.closest('details')).not.toBeNull()
  })

  it('pausing and cancelling are separate: cancel never sends a pause and never offers to resume', async () => {
    state.cancel.mutateAsync.mockResolvedValue(makeRun({ status: 'CancellationRequested' }))
    const view = renderDetail()
    fireEvent.click(screen.getByRole('button', { name: 'İptal et' }))
    fireEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'İptali onayla' }))
    await screen.findByText('İptal isteği iletildi. Yedekleme bilgisayarının işlemi durdurması bekleniyor.')
    expect(state.cancel.mutateAsync).toHaveBeenCalledTimes(1)
    expect(state.pause.mutateAsync).not.toHaveBeenCalled()
    expect(state.resume.mutateAsync).not.toHaveBeenCalled()
    state.query.data = makeRun({ status: 'CancellationRequested' })
    view.rerender(<MemoryRouter initialEntries={['/tenants/tenant-a/backup-runs/run-id']}><Routes><Route path="/tenants/:tenantId/backup-runs/:id" element={<BackupDetailPage />} /></Routes></MemoryRouter>)
    expect(screen.queryByRole('button', { name: 'Devam Et' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Duraklat' })).not.toBeInTheDocument()
  })

  it('a paused run can be resumed or cancelled, each through its own request', async () => {
    state.query.data = makeRun({ status: 'Paused', pauseReason: 'Operator' })
    state.resume.mutateAsync.mockResolvedValue(makeRun({ status: 'Pending' }))
    renderDetail()
    fireEvent.click(screen.getByRole('button', { name: 'Devam Et' }))
    await waitFor(() => expect(state.resume.mutateAsync).toHaveBeenCalledTimes(1))
    expect(state.cancel.mutateAsync).not.toHaveBeenCalled()
    expect(screen.getByRole('button', { name: 'İptal et' })).toBeEnabled()
  })
})
