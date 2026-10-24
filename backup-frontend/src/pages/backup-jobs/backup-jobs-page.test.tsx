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

const permissions = vi.hoisted(() => ({ canOperate: true }))
vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => ({ tenantId: 'tenant-a', canAdmin: true, ...permissions }) }))
vi.mock('@/features/backup-jobs/api', () => ({ getBackupJobs: vi.fn(), getBackupJob: vi.fn(), getOccurrences: vi.fn(), saveBackupJob: vi.fn(), setJobEnabled: vi.fn(), startJobRun: vi.fn() }))
beforeEach(() => {
  vi.clearAllMocks()
  permissions.canOperate = true
  vi.mocked(getBackupJobs).mockResolvedValue([job])
  vi.mocked(startJobRun).mockResolvedValue({ id: 'run-a' })
})
it('does not create a second Queue-policy run when double-click follows a fast success', async () => {
  const cache = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  render(<QueryClientProvider client={cache}><MemoryRouter><BackupJobsPage /></MemoryRouter></QueryClientProvider>)
  const button = await screen.findByRole('button', { name: 'Şimdi çalıştır' })
  fireEvent.click(button, { detail: 1 })
  expect(await screen.findByRole('link', { name: 'Denemeyi aç' })).toHaveAttribute('href', '/tenants/tenant-a/backup-runs/run-a')
  fireEvent.click(button, { detail: 2 })
  expect(startJobRun).toHaveBeenCalledTimes(1)
  cache.clear()
})
it.each([
  ['storage_connection_disabled', /(?:yeni backup başlatılamaz|yeni yedekleme başlatılamaz)/],
  ['storage_connection_unavailable', /(?:Hedef depolama bağlantısı bulunamadı|yeni backup başlatılamaz|yeni yedekleme başlatılamaz)/],
  ['quota_size_unknown', /(?:kesin depolama kotasında|kesin depolama kotası kapsamında)/],
  ['quota_bounded_upload_unsupported', /(?:Agent, yükleme öncesinde|Yedekleme bilgisayarı, yükleme öncesinde)/],
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
  fireEvent.change(screen.getByLabelText("İş, yedekleme bilgisayarı veya veritabanı ara"), { target: { value: 'SQL Agent' } })
  expect(screen.getByText('İş 0')).toBeInTheDocument()
  expect(screen.getByText('30 kayıt · Sayfa 1 / 2')).toBeInTheDocument()
  fireEvent.change(screen.getByLabelText("İş, yedekleme bilgisayarı veya veritabanı ara"), { target: { value: 'İş 2' } })
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

const scheduled: BackupJob = { ...job, id: 'job-b', name: 'Nightly', agentName: 'Ofis PC', schedule: { enabled: true, cronExpression: '17 3 * * 2', timeZone: 'Europe/Istanbul', missedRunGraceSeconds: 600, overlapPolicy: 'Skip' }, configuration: { ...job.configuration, encryptionPolicy: 'Required' }, lastRun: { id: 'run-z', status: 'Failed', createdAt: '2026-10-01T08:00:00Z', startedAt: null, completedAt: null, reasonCode: null } }

function mount() {
  const cache = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(<QueryClientProvider client={cache}><MemoryRouter><BackupJobsPage /></MemoryRouter></QueryClientProvider>)
}

it('lists name, status, database, computer and last result, and keeps schedule and encryption off the list', async () => {
  vi.mocked(getBackupJobs).mockResolvedValue([scheduled])
  mount()
  const row = within(await screen.findByRole('row', { name: /Nightly/ }))
  expect(row.getByRole('link', { name: 'Nightly' })).toHaveAttribute('href', '/tenants/tenant-a/backup-jobs/job-b/edit')
  expect(row.getByText('Etkin')).toBeInTheDocument()
  expect(row.getByText('ERP')).toBeInTheDocument()
  expect(row.getByText('Ofis PC')).toBeInTheDocument()
  expect(row.getByRole('link', { name: /Başarısız/ })).toHaveAttribute('href', '/tenants/tenant-a/backup-runs/run-z')
  for (const header of ['Zamanlama', 'Şifreleme']) expect(screen.queryByRole('columnheader', { name: header })).not.toBeInTheDocument()
  expect(screen.queryByText('17 3 * * 2')).not.toBeInTheDocument()
  expect(screen.queryByText(/Zorunlu/)).not.toBeInTheDocument()
})

it('explains in plain words what running and disabling a job do, tied to the buttons', async () => {
  mount()
  const run = await screen.findByRole('button', { name: 'Şimdi çalıştır' })
  const disable = screen.getByRole('button', { name: 'Devre dışı bırak' })
  expect(run).toHaveAccessibleDescription(/\S/)
  expect(disable).toHaveAccessibleDescription(/\S/)
  expect(disable.getAttribute('aria-describedby')).not.toBe(run.getAttribute('aria-describedby'))
})

it('sends someone without permission to their administrator instead of showing actions', async () => {
  permissions.canOperate = false
  mount()
  const row = within(await screen.findByRole('row', { name: /Queue job/ }))
  expect(row.getByText('Yöneticinize danışın')).toBeInTheDocument()
  expect(row.queryByRole('button')).not.toBeInTheDocument()
  expect(screen.queryByRole('link', { name: 'Düzenle' })).not.toBeInTheDocument()
  expect(screen.queryByRole('link', { name: /Yeni iş/ })).not.toBeInTheDocument()
})

it('an empty list has exactly one primary action', async () => {
  vi.mocked(getBackupJobs).mockResolvedValue([])
  mount()
  await screen.findByText('Henüz yedekleme işi oluşturulmadı.')
  expect(screen.getAllByRole('link', { name: /oluştur|Yeni/ })).toHaveLength(1)
})

it('on a phone shows the status beside the name and three key fields', async () => {
  setViewport(false)
  vi.mocked(getBackupJobs).mockResolvedValue([scheduled])
  mount()
  const card = within(await screen.findByRole('listitem'))
  expect(card.getByText('Etkin')).toBeInTheDocument()
  expect(card.getAllByRole('term')).toHaveLength(3)
  expect(card.getByText('Ofis PC')).toBeInTheDocument()
  expect(card.queryByText('17 3 * * 2')).not.toBeInTheDocument()
})
