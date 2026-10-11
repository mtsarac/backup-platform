import { fireEvent, render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, expect, it, vi } from 'vitest'
import { BackupJobsPage } from './backup-jobs-page'
import { getBackupJobs, startJobRun } from '@/features/backup-jobs/api'
import { ApiError } from '@/lib/api/client'

vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => ({ tenantId: 'tenant-a', canOperate: true, canAdmin: true }) }))
vi.mock('@/features/backup-jobs/api', () => ({ getBackupJobs: vi.fn(), getBackupJob: vi.fn(), getOccurrences: vi.fn(), saveBackupJob: vi.fn(), setJobEnabled: vi.fn(), startJobRun: vi.fn() }))
beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(getBackupJobs).mockResolvedValue([{ id: 'job-a', name: 'Queue job', agentId: 'agent-a', configuration: { engine: 'mssql', database: 'ERP', host: 'sql', port: null, integratedSecurity: true, databaseCredentialReference: null, encrypt: true, trustServerCertificate: false, stagingReference: 'default', storageProvider: 'local', storagePrefix: '', storageExecutionLocation: 'Agent', copyOnly: true, checksumPolicy: 'Required', compressionPolicy: 'Preferred' }, retryPolicy: { maxAttempts: 3, initialDelaySeconds: 30, maxDelaySeconds: 300, backoff: 'Exponential', jitter: 0 }, overlapPolicy: 'Queue', schedule: null, enabled: true, backupType: 'mssql-full', createdAt: '2026-10-06T10:00:00Z' }])
  vi.mocked(startJobRun).mockResolvedValue({ id: 'run-a' })
})
it('does not create a second Queue-policy run when double-click follows a fast success', async () => {
  const cache = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  render(<QueryClientProvider client={cache}><MemoryRouter><BackupJobsPage /></MemoryRouter></QueryClientProvider>)
  const button = await screen.findByRole('button', { name: 'Şimdi çalıştır' })
  fireEvent.click(button, { detail: 1 })
  expect(await screen.findByRole('link', { name: 'Çalıştırmayı aç' })).toHaveAttribute('href', '/tenants/tenant-a/backup-runs/run-a')
  fireEvent.click(button, { detail: 2 })
  expect(startJobRun).toHaveBeenCalledTimes(1)
  cache.clear()
})
it.each([
  ['storage_connection_disabled', 'Hedef depolama bağlantısı devre dışı; yeni backup başlatılamaz.'],
  ['storage_connection_unavailable', 'Hedef depolama bağlantısı bulunamadı veya bu Job\'un Storage Provider\'ına ait değil; yeni backup başlatılamaz.'],
  ['quota_size_unknown', 'Backup boyutu henüz bilinmediği için kesin depolama kotasında güvenli alan ayrılamadı; yeni backup başlatılmadı. Boyut tahmininin belirlenmesi için yöneticiye başvurun.'],
  ['quota_bounded_upload_unsupported', 'Agent, yükleme öncesinde kesin depolama kotasını uygulamayı desteklemiyor; Agent\'ı güncelleyin. Yeni backup başlatılmadı.'],
])('Şimdi çalıştır, %s reddini Türkçe gösterir', async (code, text) => {
  vi.mocked(startJobRun).mockRejectedValue(new ApiError(409, { error: 'admission_denied', code }))
  const cache = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  render(<QueryClientProvider client={cache}><MemoryRouter><BackupJobsPage /></MemoryRouter></QueryClientProvider>)
  fireEvent.click(await screen.findByRole('button', { name: 'Şimdi çalıştır' }), { detail: 1 })
  expect(await screen.findByRole('alert')).toHaveTextContent(text)
  cache.clear()
})
