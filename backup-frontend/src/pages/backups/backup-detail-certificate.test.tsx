import { fireEvent, screen, waitFor } from '@testing-library/react'
import { render } from '@/test/render'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { getBackupJob, saveBackupJob, startJobRun } from '@/features/backup-jobs/api'
import { savedJob } from '@/features/backup-jobs/test-fixtures'
import { makeRun } from '@/features/backups/test-data'
import type { BackupRun } from '@/features/backups/types'
import { ApiError } from '@/lib/api/client'
import { BackupDetailPage } from './backup-detail-page'

const state = vi.hoisted(() => ({ canOperate: true, run: undefined as BackupRun | undefined }))
vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => ({ tenantId: 'tenant-a', canOperate: state.canOperate, canAdmin: true }) }))
vi.mock('@/features/backups/hooks', () => {
  const action = { isPending: false, error: null, mutateAsync: vi.fn(), reset: vi.fn() }
  return { useBackup: () => ({ data: state.run, isPending: false, error: null }), useBackupActions: () => ({ retry: action, cancel: action, pause: action, resume: action }) }
})
vi.mock('@/features/backup-jobs/api')
vi.mock('@/pages/artifacts/artifact-detail-page', () => ({ ArtifactDetail: () => null }))

const trustTitle = 'Bu sunucunun sertifikasına yine de güven'

function renderDetail() {
  render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })}>
    <MemoryRouter initialEntries={['/tenants/tenant-a/backup-runs/run-id']}><Routes>
      <Route path="/tenants/:tenantId/backup-runs/:id" element={<BackupDetailPage />} />
    </Routes></MemoryRouter>
  </QueryClientProvider>)
}

beforeEach(() => {
  vi.clearAllMocks()
  state.canOperate = true
  state.run = makeRun({ status: 'Failed', reasonCode: 'ServerCertificateUntrusted' })
  vi.mocked(getBackupJob).mockResolvedValue({ ...savedJob, id: 'job-id' })
  vi.mocked(saveBackupJob).mockResolvedValue({} as never)
  vi.mocked(startJobRun).mockResolvedValue({ id: 'run-2' })
})

describe('trusting the server certificate from a failed backup', () => {
  it('shows the closed step and the risk sentence only for the certificate failure', () => {
    renderDetail()
    expect(screen.getByText(trustTitle).closest('details')).not.toHaveAttribute('open')
    expect(screen.getByText('Bu sunucunun kimliği doğrulanamaz; yalnızca kendi sunucunuz olduğundan eminseniz açın.')).toBeInTheDocument()
  })

  it.each(['DatabaseUnavailable', 'OperationTimedOut', 'AuthenticationFailed', 'InvalidConfiguration', 'connection_failed'])('shows nothing to trust for the general %s failure', (reasonCode) => {
    state.run = makeRun({ status: 'Failed', reasonCode })
    renderDetail()
    expect(screen.queryByText(trustTitle)).not.toBeInTheDocument()
    expect(screen.queryByText(/yalnızca kendi sunucunuz olduğundan eminseniz/)).not.toBeInTheDocument()
  })

  it('offers it to nobody who cannot operate', () => {
    state.canOperate = false
    renderDetail()
    expect(screen.queryByText(trustTitle)).not.toBeInTheDocument()
  })

  it('says the trust stays on the job, saves the job as it is with the trust on, then runs it again', async () => {
    renderDetail()
    fireEvent.click(screen.getByText(trustTitle))
    expect(screen.getByText('Bu işte sunucu sertifikası kalıcı olarak güvenilir sayılacak.')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Güven ve yeniden dene' }))
    await waitFor(() => expect(startJobRun).toHaveBeenCalledWith('tenant-a', 'job-id', expect.any(String)))
    const { name, agentId, configuration, retryPolicy, overlapPolicy, schedule, retention } = savedJob
    expect(saveBackupJob).toHaveBeenCalledWith('tenant-a', { name, agentId, configuration: { ...configuration, trustServerCertificate: true }, retryPolicy, overlapPolicy, schedule, retention }, 'job-id')
    expect(vi.mocked(saveBackupJob).mock.invocationCallOrder[0]).toBeLessThan(vi.mocked(startJobRun).mock.invocationCallOrder[0])
  })

  it('does not run again when saving the job fails, and says why', async () => {
    vi.mocked(saveBackupJob).mockRejectedValue(new ApiError(409, { error: 'concurrent_update' }))
    renderDetail()
    fireEvent.click(screen.getByText(trustTitle))
    fireEvent.click(screen.getByRole('button', { name: 'Güven ve yeniden dene' }))
    await waitFor(() => expect(screen.getAllByRole('alert')).toHaveLength(2))
    expect(startJobRun).not.toHaveBeenCalled()
  })
})
