import { fireEvent, screen, within } from '@testing-library/react'
import { render } from '@/test/render'
import { setViewport } from '@/test/viewport'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, expect, it, vi } from 'vitest'
import { BackupJobsPage } from './backup-jobs-page'
import { getBackupJobs, startJobRun } from '@/features/backup-jobs/api'
import { ApiError } from '@/lib/api/client'
import type { BackupJob } from '@/features/backup-jobs/types'

const job: BackupJob = { id: 'job-a', name: 'Queue job', agentId: 'agent-a', configuration: { engine: 'mssql', database: 'ERP', host: 'sql', port: null, integratedSecurity: true, databaseCredentialReference: null, encrypt: true, trustServerCertificate: false, stagingReference: 'default', storagePrefix: '', destinations: [{ storageProvider: 'local', storageConnectionId: null, storageExecutionLocation: 'Agent', isPrimary: true }], requiredVerifiedReplicaCount: 1, copyOnly: true, checksumPolicy: 'Required', compressionPolicy: 'Preferred', encryptionPolicy: 'Off' }, retryPolicy: { maxAttempts: 3, initialDelaySeconds: 30, maxDelaySeconds: 300, backoff: 'Exponential', jitter: 0 }, overlapPolicy: 'Queue', schedule: null, enabled: true, backupType: 'mssql-full', createdAt: '2026-10-06T10:00:00Z' }

vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => ({ tenantId: 'tenant-a', canOperate: true, canAdmin: true }) }))
vi.mock('@/features/backup-jobs/api', () => ({ getBackupJobs: vi.fn(), getBackupJob: vi.fn(), getOccurrences: vi.fn(), saveBackupJob: vi.fn(), setJobEnabled: vi.fn(), startJobRun: vi.fn() }))
beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(getBackupJobs).mockResolvedValue([job])
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
  ['storage_connection_disabled', /(?:yeni backup başlatılamaz|yeni yedekleme başlatılamaz)/],
  ['storage_connection_unavailable', /(?:Hedef depolama bağlantısı bulunamadı|yeni backup başlatılamaz|yeni yedekleme başlatılamaz)/],
  ['quota_size_unknown', /(?:kesin depolama kotasında|kesin depolama kotası kapsamında)/],
  ['quota_bounded_upload_unsupported', /(?:Agent, yükleme öncesinde|Ajan, yükleme öncesinde)/],
])('Şimdi çalıştır, %s reddini Türkçe gösterir', async (code, text) => {
  vi.mocked(startJobRun).mockRejectedValue(new ApiError(409, { error: 'admission_denied', code }))
  const cache = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  render(<QueryClientProvider client={cache}><MemoryRouter><BackupJobsPage /></MemoryRouter></QueryClientProvider>)
  fireEvent.click(await screen.findByRole('button', { name: 'Şimdi çalıştır' }), { detail: 1 })
  expect(await screen.findByRole('alert')).toHaveTextContent(text)
  cache.clear()
})

it('sayfalar 25 iş gösterir; durum ve arama değişiklikleri ilk sayfaya döner', async () => {
  vi.mocked(getBackupJobs).mockResolvedValue(Array.from({ length: 60 }, (_, index) => ({ ...job, id: `job-${index}`, name: `İş ${index}`, agentName: 'SQL Agent', enabled: index % 2 === 0 })))
  const cache = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  render(<QueryClientProvider client={cache}><MemoryRouter><BackupJobsPage /></MemoryRouter></QueryClientProvider>)
  expect(await screen.findByText('İş 0')).toBeInTheDocument()
  expect(screen.getAllByRole('row')).toHaveLength(26)
  expect(screen.queryByText('İş 25')).not.toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: 'Sonraki' }))
  expect(screen.getByText('İş 25')).toBeInTheDocument()
  expect(screen.queryByText('İş 0')).not.toBeInTheDocument()
  fireEvent.change(screen.getByLabelText('Durum'), { target: { value: 'enabled' } })
  expect(screen.getByText('İş 0')).toBeInTheDocument()
  expect(screen.queryByText('İş 25')).not.toBeInTheDocument()
  expect(screen.getByText('30 kayıt · Sayfa 1 / 2')).toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: 'Sonraki' }))
  fireEvent.change(screen.getByLabelText("İş, ajan veya veritabanı ara"), { target: { value: 'SQL Agent' } })
  expect(screen.getByText('İş 0')).toBeInTheDocument()
  expect(screen.getByText('30 kayıt · Sayfa 1 / 2')).toBeInTheDocument()
  fireEvent.change(screen.getByLabelText("İş, ajan veya veritabanı ara"), { target: { value: 'İş 2' } })
  expect(screen.getByText('İş 2')).toBeInTheDocument()
  expect(screen.queryByText('İş 0')).not.toBeInTheDocument()
  cache.clear()
})

it('shows each job as a card with its name, status, key fields and actions on a phone', async () => {
  setViewport(false)
  const cache = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  render(<QueryClientProvider client={cache}><MemoryRouter><BackupJobsPage /></MemoryRouter></QueryClientProvider>)
  const card = within(await screen.findByRole('listitem'))
  expect(screen.queryByRole('table')).not.toBeInTheDocument()
  expect(card.getByRole('heading', { name: /Queue job/ })).toBeInTheDocument()
  expect(card.getByText('ERP')).toBeInTheDocument()
  expect(card.getAllByRole('term').length).toBeLessThanOrEqual(3)
  expect(card.getByRole('button', { name: 'Şimdi çalıştır' })).toBeInTheDocument()
  cache.clear()
})
