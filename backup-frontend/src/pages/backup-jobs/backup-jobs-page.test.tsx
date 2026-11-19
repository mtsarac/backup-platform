import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { render } from '@/test/render'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { LocationProbe } from '@/features/platform/location-probe'
import { beforeEach, expect, it, vi } from 'vitest'
import { BackupJobsPage } from './backup-jobs-page'
import { archiveBackupJob, getBackupJobHealth, getBackupJobs, getHealthTimeline, setJobEnabled, startJobRun } from '@/features/backup-jobs/api'
import { healthyJobHealth, savedJob } from '@/features/backup-jobs/test-fixtures'
import { getTenantConnections, getTenantProviders } from '@/features/storage-connections/api'
import { catalog, makeConnection } from '@/features/storage-connections/test-fixtures'
import { getAgents } from '@/features/agents/api'
import { getBackup, getBackups } from '@/features/backups/api'
import { makeRun } from '@/features/backups/test-data'
import { ApiError } from '@/lib/api/client'
import type { Agent } from '@/features/agents/types'
import type { BackupJob } from '@/features/backup-jobs/types'
import type { BackupJobHealth } from '@/features/backup-jobs/health'

const job = savedJob
const other = { ...job, id: 'job-b', name: 'Satış', configuration: { ...job.configuration, database: 'CRM' } } as BackupJob
const critical: BackupJobHealth = { ...healthyJobHealth, state: 'Critical', factors: { ...healthyJobHealth.factors, freshness: { ...healthyJobHealth.factors.freshness, score: 0, reason: 'backup_stale' } } }

const permissions = vi.hoisted(() => ({ canOperate: true, canAdmin: true }))
vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => ({ tenantId: 'tenant-a', ...permissions }) }))
vi.mock('@/features/backup-jobs/api')
vi.mock('@/features/agents/api')
vi.mock('@/features/backups/api')
vi.mock('@/features/storage-connections/api')

const agent = (connectivity: Agent['connectivity']) => [{ agentId: 'agent-a', connectivity }] as Agent[]
function mount(url = '/tenants/tenant-a/backup-jobs/job-a') {
  const cache = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const router = createMemoryRouter([{ path: '/tenants/:tenantId/backup-jobs/:id?', element: <><BackupJobsPage /><LocationProbe /></> }], { initialEntries: [url] })
  return render(<QueryClientProvider client={cache}><RouterProvider router={router} /></QueryClientProvider>)
}

beforeEach(() => {
  vi.clearAllMocks()
  Object.assign(permissions, { canOperate: true, canAdmin: true })
  vi.mocked(getBackupJobs).mockResolvedValue([job, other])
  vi.mocked(getBackupJobHealth).mockResolvedValue(healthyJobHealth)
  vi.mocked(getHealthTimeline).mockResolvedValue({ jobs: [] })
  vi.mocked(getAgents).mockResolvedValue(agent('online'))
  vi.mocked(getBackups).mockResolvedValue({ items: [], totalCount: 1, page: 1, pageSize: 1 })
  vi.mocked(startJobRun).mockResolvedValue({ id: 'run-a' })
  vi.mocked(getTenantConnections).mockResolvedValue({ items: [makeConnection()] })
  vi.mocked(getTenantProviders).mockResolvedValue({ items: catalog })
})

it('opens the job with the list beside it and marks it current', async () => {
  mount()
  expect(await screen.findByRole('link', { name: /Muhasebe/, current: 'page' })).toHaveAttribute('href', '/tenants/tenant-a/backup-jobs/job-a')
  expect(screen.getByRole('link', { name: /Satış/ })).not.toHaveAttribute('aria-current')
  expect(await screen.findByRole('heading', { name: 'Muhasebe' })).toBeInTheDocument()
  expect(screen.getByRole('link', { name: 'Tüm denemeler' })).toHaveAttribute('href', '/tenants/tenant-a/backup-runs?jobId=job-a')
})

it('shows no job detail until one is chosen, and the way back on a narrow screen', async () => {
  mount('/tenants/tenant-a/backup-jobs')
  expect(await screen.findByText('Ayrıntıları görmek için bir iş seçin.')).toBeInTheDocument()
  fireEvent.click(screen.getByRole('link', { name: /Satış/ }))
  expect(await screen.findByRole('link', { name: 'İşlere dön' })).toHaveAttribute('href', '/tenants/tenant-a/backup-jobs')
})

it.each([
  ['Critical and stale', { health: critical }, 'Dikkat gerekiyor'],
  ['offline computer', { connectivity: 'offline' as const }, 'Dikkat gerekiyor'],
  ['healthy', {}, 'Güncel'],
  ['paused', { jobs: [{ ...job, enabled: false }, other], health: critical }, 'Durduruldu'],
  ['manual', { jobs: [{ ...job, schedule: null }, other], health: critical }, 'Elle çalışır'],
  ['no success yet', { total: 0, health: critical }, 'İlk yedek bekleniyor'],
  ['running', { jobs: [{ ...job, lastRun: { id: 'run-r', status: 'Running', createdAt: '', startedAt: null, completedAt: null, reasonCode: null } }, other] }, 'Çalışıyor %35'],
])('says one sentence for the status: %s', async (_name, setup: { health?: BackupJobHealth; connectivity?: Agent['connectivity']; jobs?: BackupJob[]; total?: number }, label) => {
  if (setup.health) vi.mocked(getBackupJobHealth).mockResolvedValue(setup.health)
  if (setup.connectivity) vi.mocked(getAgents).mockResolvedValue(agent(setup.connectivity))
  if (setup.jobs) vi.mocked(getBackupJobs).mockResolvedValue(setup.jobs)
  if (setup.total !== undefined) vi.mocked(getBackups).mockResolvedValue({ items: [], totalCount: setup.total, page: 1, pageSize: 1 })
  vi.mocked(getBackup).mockResolvedValue(makeRun({ id: 'run-r' }))
  mount()
  await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent(label))
})

it('gives the reason and the matching action when the computer is offline', async () => {
  vi.mocked(getAgents).mockResolvedValue(agent('offline'))
  mount()
  await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('Yedekleme bilgisayarı çevrimdışı.'))
  expect(screen.getByRole('link', { name: 'Bilgisayarı kontrol et' })).toHaveAttribute('href', '/tenants/tenant-a/agents/agent-a')
})

it('starts a backup with "Şimdi yedekle" and links to the new attempt; a double click starts one', async () => {
  mount()
  const button = await screen.findByRole('button', { name: 'Şimdi yedekle' })
  fireEvent.click(button, { detail: 1 })
  expect(await screen.findByRole('link', { name: 'Denemeyi aç' })).toHaveAttribute('href', '/tenants/tenant-a/backup-runs/run-a')
  fireEvent.click(button, { detail: 2 })
  expect(startJobRun).toHaveBeenCalledTimes(1)
  expect(startJobRun).toHaveBeenCalledWith('tenant-a', 'job-a', expect.any(String))
})

it('hides the actions from someone who cannot operate, keeping the status', async () => {
  permissions.canOperate = false
  mount()
  await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('Güncel'))
  expect(screen.queryByRole('button', { name: 'Şimdi yedekle' })).not.toBeInTheDocument()
  expect(screen.queryByRole('button', { name: /Zamanlamayı/ })).not.toBeInTheDocument()
})

it.each([
  ['storage_connection_disabled', /yeni yedekleme başlatılamaz/],
  ['quota_size_unknown', /kesin depolama kotası/],
])('explains a refused start (%s) in Turkish with the code under technical details', async (code, text) => {
  vi.mocked(startJobRun).mockRejectedValue(new ApiError(409, { error: 'admission_denied', code }))
  mount()
  fireEvent.click(await screen.findByRole('button', { name: 'Şimdi yedekle' }), { detail: 1 })
  const alert = await screen.findByRole('alert')
  expect(alert).toHaveTextContent(text)
  expect(within(alert).queryByText(code)).toBeNull()
  await userEvent.click(within(alert).getByRole('button', { name: 'Teknik ayrıntılar' }))
  expect(within(await screen.findByRole('dialog')).getByText(code)).toBeVisible()
})

it('points to the running attempt when another one already runs', async () => {
  vi.mocked(startJobRun).mockRejectedValue(new ApiError(409, { error: 'run_already_active', activeRunId: 'run-x' }))
  mount()
  fireEvent.click(await screen.findByRole('button', { name: 'Şimdi yedekle' }), { detail: 1 })
  expect(await screen.findByRole('link', { name: 'Süren denemeyi aç' })).toHaveAttribute('href', '/tenants/tenant-a/backup-runs/run-x')
})

it('pauses the schedule and resumes it, as separate actions', async () => {
  vi.mocked(setJobEnabled).mockResolvedValue(job)
  mount()
  fireEvent.click(await screen.findByRole('button', { name: 'Zamanlamayı durdur' }))
  await waitFor(() => expect(setJobEnabled).toHaveBeenCalledWith('tenant-a', 'job-a', false))
})

it('resumes a paused job with "Zamanlamayı sürdür"', async () => {
  vi.mocked(getBackupJobs).mockResolvedValue([{ ...job, enabled: false }])
  vi.mocked(setJobEnabled).mockResolvedValue(job)
  mount()
  fireEvent.click(await screen.findByRole('button', { name: 'Zamanlamayı sürdür' }))
  await waitFor(() => expect(setJobEnabled).toHaveBeenCalledWith('tenant-a', 'job-a', true))
  await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('Durduruldu'))
})

it('archives only after the name is typed, then the job leaves the list', async () => {
  vi.mocked(archiveBackupJob).mockResolvedValue(undefined)
  mount()
  fireEvent.click(await screen.findByRole('button', { name: 'Diğer' }))
  fireEvent.click(await screen.findByRole('menuitem', { name: 'Korunan veriyi kaldır…' }))
  const dialog = within(await screen.findByRole('dialog'))
  expect(dialog.getByText('Yedekleriniz silinmez; bu iş artık çalışmaz ve listede görünmez.')).toBeInTheDocument()
  const confirm = dialog.getByRole('button', { name: 'Korunan veriyi kaldır' })
  expect(confirm).toBeDisabled()
  vi.mocked(getBackupJobs).mockResolvedValue([other])
  await userEvent.type(dialog.getByLabelText(/Onaylamak için adı yazın/), 'Muhasebe')
  expect(confirm).toBeEnabled()
  fireEvent.click(confirm)
  await waitFor(() => expect(archiveBackupJob).toHaveBeenCalledWith('tenant-a', 'job-a'))
  await waitFor(() => expect(screen.queryByRole('link', { name: /Muhasebe/ })).not.toBeInTheDocument())
  expect(screen.getByTestId('location')).toHaveTextContent(/^\/tenants\/tenant-a\/backup-jobs$/)
})

it('offers removal only to administrators', async () => {
  permissions.canAdmin = false
  mount()
  await screen.findByRole('heading', { name: 'Muhasebe' })
  expect(screen.queryByRole('button', { name: 'Diğer' })).not.toBeInTheDocument()
})

it('shows no score breakdown or change summary', async () => {
  mount()
  await screen.findByRole('status')
  expect(screen.queryByText(/\/ 100/)).not.toBeInTheDocument()
  expect(screen.queryByRole('region', { name: 'Yedekleme sağlığı' })).not.toBeInTheDocument()
  expect(screen.queryByText(/Değişen bölümler/)).not.toBeInTheDocument()
})

it('says the job was not found for an unknown id', async () => {
  mount('/tenants/tenant-a/backup-jobs/missing')
  expect(await screen.findByText('Yedekleme işi bulunamadı.')).toBeInTheDocument()
})

it('an empty account offers the one action to create a job', async () => {
  vi.mocked(getBackupJobs).mockResolvedValue([])
  mount('/tenants/tenant-a/backup-jobs')
  expect(await screen.findByText('Henüz yedekleme işi oluşturulmadı.')).toBeInTheDocument()
  expect(screen.getAllByRole('link', { name: 'Yedekleme işi oluştur' })).toHaveLength(1)
})

it('an empty account sends someone without permission to their administrator', async () => {
  permissions.canOperate = false
  vi.mocked(getBackupJobs).mockResolvedValue([])
  mount('/tenants/tenant-a/backup-jobs')
  expect(await screen.findByText('Yöneticinize danışın')).toBeInTheDocument()
  expect(screen.queryByRole('link', { name: 'Yedekleme işi oluştur' })).not.toBeInTheDocument()
})

it('an empty filter result offers "Filtreleri temizle", and filters live in the URL', async () => {
  mount('/tenants/tenant-a/backup-jobs?search=yok')
  expect(await screen.findByText('Aramanızla eşleşen iş yok.')).toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: 'Filtreleri temizle' }))
  expect(screen.getByTestId('location')).toHaveTextContent(/^\/tenants\/tenant-a\/backup-jobs$/)
  expect(await screen.findByRole('link', { name: /Muhasebe/ })).toBeInTheDocument()
})

it('filters by state and search', async () => {
  vi.mocked(getBackupJobs).mockResolvedValue([job, { ...other, enabled: false }])
  mount('/tenants/tenant-a/backup-jobs')
  fireEvent.change(await screen.findByLabelText('Durum'), { target: { value: 'disabled' } })
  expect(screen.queryByRole('link', { name: /Muhasebe/ })).not.toBeInTheDocument()
  expect(screen.getByRole('link', { name: /Satış/ })).toBeInTheDocument()
  expect(screen.getByTestId('location')).toHaveTextContent('state=disabled')
})

it('a failed load says what happened and keeps the code under technical details', async () => {
  vi.mocked(getBackupJobs).mockRejectedValue(new ApiError(503, { error: 'storage_unavailable' }))
  mount('/tenants/tenant-a/backup-jobs')
  const alert = await screen.findByRole('alert')
  expect(alert).toHaveTextContent('Mevcut yedekleriniz etkilenmedi')
  expect(within(alert).queryByText('storage_unavailable')).toBeNull()
})
