import { fireEvent, screen, waitFor } from '@testing-library/react'
import { render } from '@/test/render'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { BackupJobWizardPage } from './backup-job-wizard-page'
import { BackupJobsPage } from './backup-jobs-page'
import { getBackupJob, getBackupJobs, getOccurrences, saveBackupJob } from '@/features/backup-jobs/api'
import type { BackupJob } from '@/features/backup-jobs/types'
import { ApiError } from '@/lib/api/client'
import { getTenantConnections, getTenantProviders } from '@/features/storage-connections/api'
import { catalog, makeConnection, makeManaged } from '@/features/storage-connections/test-fixtures'

vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => ({ tenantId: 'tenant-a', canOperate: true, canAdmin: true }) }))
vi.mock('@/features/agents/api', () => ({ getAgents: vi.fn().mockResolvedValue([{ agentId: 'agent-a', hostName: 'SQL Agent', operationalStatus: 'Online' }]) }))
vi.mock('@/features/storage-connections/api')
vi.mock('@/features/backup-jobs/api', () => ({ saveBackupJob: vi.fn(), getBackupJob: vi.fn(), getBackupJobs: vi.fn(), getOccurrences: vi.fn(), startJobRun: vi.fn(), setJobEnabled: vi.fn() }))

const job: BackupJob = {
  id: 'job-a', backupType: 'Full', createdAt: '2026-10-01T08:00:00Z', name: 'ERP backup', agentId: 'agent-a', agentName: 'SQL Agent', enabled: true, overlapPolicy: 'Skip', schedule: null, nextOccurrenceAt: null, lastRun: null,
  configuration: { engine: 'mssql', database: 'ERP', host: 'sql', port: null, integratedSecurity: true, databaseCredentialReference: null, encrypt: true, trustServerCertificate: false, stagingReference: 'default', storagePrefix: '', destinations: [{ storageProvider: 'local', storageConnectionId: 'conn-local', storageExecutionLocation: 'Agent', isPrimary: true }], requiredVerifiedReplicaCount: 1, copyOnly: true, checksumPolicy: 'Required', compressionPolicy: 'Preferred', encryptionPolicy: 'Off' },
  retryPolicy: { maxAttempts: 3, initialDelaySeconds: 30, maxDelaySeconds: 300, backoff: 'Exponential', jitter: 0.2 },
  retention: { enabled: true, keepForSeconds: 1209600, deleteGracePeriodSeconds: 86400 },
}
const withPolicy = (encryptionPolicy: BackupJob['configuration']['encryptionPolicy']): BackupJob => ({ ...job, configuration: { ...job.configuration, encryptionPolicy } })

function renderWizard(route = '/tenants/tenant-a/backup-jobs/new') {
  const cache = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(<QueryClientProvider client={cache}><MemoryRouter initialEntries={[route]}><Routes>
    <Route path="/tenants/:tenantId/backup-jobs/new" element={<BackupJobWizardPage />} />
    <Route path="/tenants/:tenantId/backup-jobs/:id/edit" element={<BackupJobWizardPage />} />
  </Routes></MemoryRouter></QueryClientProvider>)
}
const next = () => fireEvent.click(screen.getByRole('button', { name: 'İleri' }))
beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(getTenantConnections).mockResolvedValue({ items: [makeConnection(), makeManaged()] })
  vi.mocked(getTenantProviders).mockResolvedValue({ items: catalog })
  vi.mocked(getOccurrences).mockResolvedValue([])
  vi.mocked(saveBackupJob).mockRejectedValue(new ApiError(400))
})
/** Fill the required fields and stop on the "Saklama & seçenekler" step. */
async function reachOptionsStep() {
  await screen.findByRole('option', { name: /(?:SQL Agent|SQL Ajanı)/ })
  fireEvent.change(screen.getByLabelText('İş adı'), { target: { value: 'ERP backup' } })
  fireEvent.change(screen.getByLabelText('Ajan'), { target: { value: 'agent-a' } })
  next()
  fireEvent.change(screen.getByLabelText('SQL Server ana bilgisayarı / veritabanı sunucusu'), { target: { value: 'sql' } })
  fireEvent.change(screen.getByLabelText('Veritabanı'), { target: { value: 'ERP' } })
  next(); next(); next()
  await screen.findByLabelText('Yedek şifreleme politikası')
}

describe('encryption policy in the job wizard', () => {
  it('defaults to Off and sends it with the configuration', async () => {
    renderWizard()
    await reachOptionsStep()
    expect(screen.getByLabelText('Yedek şifreleme politikası')).toHaveValue('Off')
    next()
    await waitFor(() => expect(screen.getByRole('button', { name: 'Kaydet' })).toBeEnabled())
    fireEvent.click(screen.getByRole('button', { name: 'Kaydet' }))
    await waitFor(() => expect(saveBackupJob).toHaveBeenCalled())
    expect(vi.mocked(saveBackupJob).mock.calls[0][1].configuration.encryptionPolicy).toBe('Off')
  })

  it('offers Off / Optional / Required, explains the choice and carries Required into the summary and the request', async () => {
    renderWizard()
    await reachOptionsStep()
    const select = screen.getByLabelText('Yedek şifreleme politikası')
    expect([...(select as HTMLSelectElement).options].map(option => option.value)).toEqual(['Off', 'Optional', 'Required'])
    fireEvent.change(select, { target: { value: 'Required' } })
    expect(screen.getByText(/Yedek yalnızca şifreli \(v2-bpe\) gelirse kabul edilir/)).toBeInTheDocument()
    next()
    expect(screen.getByText('Zorunlu · şifrelenemezse başarısız')).toBeInTheDocument()
    await waitFor(() => expect(screen.getByRole('button', { name: 'Kaydet' })).toBeEnabled())
    fireEvent.click(screen.getByRole('button', { name: 'Kaydet' }))
    await waitFor(() => expect(saveBackupJob).toHaveBeenCalled())
    expect(vi.mocked(saveBackupJob).mock.calls[0][1].configuration.encryptionPolicy).toBe('Required')
  })

  it('keeps the saved policy when editing and routes a server field error to its step', async () => {
    vi.mocked(getBackupJob).mockResolvedValue(withPolicy('Optional'))
    vi.mocked(saveBackupJob).mockRejectedValue(new ApiError(400, { field: 'configuration.encryptionPolicy', code: 'invalid_value' }))
    renderWizard('/tenants/tenant-a/backup-jobs/job-a/edit')
    await waitFor(() => expect(screen.getByRole('button', { name: 'Kaydet' })).toBeEnabled())
    fireEvent.click(screen.getByRole('button', { name: 'Kaydet' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Hatalı alana git: Saklama & seçenekler' }))
    expect(await screen.findByLabelText('Yedek şifreleme politikası')).toHaveValue('Optional')
    expect(document.getElementById('configuration.encryptionPolicy-error')).toHaveTextContent(/.+/)
  })
})

describe('encryption policy in the job list', () => {
  it('shows each job policy', async () => {
    vi.mocked(getBackupJobs).mockResolvedValue([job, { ...withPolicy('Required'), id: 'job-b', name: 'Billing backup' }])
    const cache = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    render(<QueryClientProvider client={cache}><MemoryRouter><BackupJobsPage /></MemoryRouter></QueryClientProvider>)
    const rows = await screen.findAllByRole('row')
    expect(rows[1]).toHaveTextContent('Kapalı')
    expect(rows[2]).toHaveTextContent('Zorunlu')
  })
})
