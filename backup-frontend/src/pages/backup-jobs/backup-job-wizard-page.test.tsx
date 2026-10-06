import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { BackupJobWizardPage } from './backup-job-wizard-page'
import { saveBackupJob } from '@/features/backup-jobs/api'
import { ApiError } from '@/lib/api/client'

const access = vi.hoisted(() => ({ tenantId: 'tenant-a', canOperate: true, canAdmin: true }))
vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => access }))
vi.mock('@/features/agents/api', () => ({ getAgents: vi.fn().mockResolvedValue([{ agentId: 'agent-a', hostName: 'SQL Agent', operationalStatus: 'Online' }]) }))
vi.mock('@/features/backup-jobs/api', () => ({ saveBackupJob: vi.fn(), getBackupJob: vi.fn(), getBackupJobs: vi.fn(), getOccurrences: vi.fn(), startJobRun: vi.fn(), setJobEnabled: vi.fn() }))
function renderPage() {
  return render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })}><MemoryRouter><BackupJobWizardPage /></MemoryRouter></QueryClientProvider>)
}
beforeEach(() => { vi.clearAllMocks(); access.canOperate = true; access.canAdmin = true })
describe('iş formu', () => {
  it('Viewer rolüne düzenleme formunu sunmaz', () => {
    access.canOperate = false
    renderPage()
    expect(screen.getByRole('alert')).toHaveTextContent('yetkiniz yok')
    expect(screen.queryByRole('button', { name: 'Kaydet' })).not.toBeInTheDocument()
  })
  it('cron alanını yerel olarak yeniden doğrulamaz ve sunucu alan hatasını gösterir', async () => {
    vi.mocked(saveBackupJob).mockRejectedValue(new ApiError(400, { field: 'cronExpression', code: 'invalid_cron' }))
    renderPage()
    fireEvent.change(screen.getByLabelText('Cron ifadesi'), { target: { value: 'invalid cron' } })
    fireEvent.click(screen.getByRole('button', { name: 'Kaydet' }))
    await waitFor(() => expect(saveBackupJob).toHaveBeenCalled())
    expect(vi.mocked(saveBackupJob).mock.calls[0][1].schedule?.cronExpression).toBe('invalid cron')
    await waitFor(() => expect(document.getElementById('cronExpression-error')).toHaveTextContent(/.+/))
    expect(screen.getByLabelText('Cron ifadesi')).toHaveAttribute('aria-invalid', 'true')
  })
  it('saklama süresi hatasını sunucudan alır', async () => {
    vi.mocked(saveBackupJob).mockRejectedValue(new ApiError(400, { field: 'retention.keepForSeconds', code: 'invalid_request' }))
    renderPage()
    fireEvent.change(screen.getByLabelText('KeepFor — saklama süresi (saniye)'), { target: { value: '-1' } })
    fireEvent.click(screen.getByRole('button', { name: 'Kaydet' }))
    await waitFor(() => expect(saveBackupJob).toHaveBeenCalled())
    expect(vi.mocked(saveBackupJob).mock.calls[0][1].retention?.keepForSeconds).toBe(-1)
    await waitFor(() => expect(document.getElementById('retention.keepForSeconds-error')).toHaveTextContent(/.+/))
  })
  it('Operator saklama politikasını değiştiremez veya gönderemez', async () => {
    access.canAdmin = false
    vi.mocked(saveBackupJob).mockRejectedValue(new ApiError(400))
    renderPage()
    expect(screen.getByLabelText('KeepFor — saklama süresi (saniye)')).toBeDisabled()
    fireEvent.click(screen.getByRole('button', { name: 'Kaydet' }))
    await waitFor(() => expect(saveBackupJob).toHaveBeenCalled())
    expect(vi.mocked(saveBackupJob).mock.calls[0][1].retention).toBeUndefined()
  })
})
