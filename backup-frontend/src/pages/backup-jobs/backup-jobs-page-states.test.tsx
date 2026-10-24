import { fireEvent, screen, within } from '@testing-library/react'
import { render } from '@/test/render'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, expect, it, vi } from 'vitest'
import { BackupJobsPage } from './backup-jobs-page'
import { getBackupJobs, startJobRun } from '@/features/backup-jobs/api'
import { ApiError } from '@/lib/api/client'
import type { BackupJob } from '@/features/backup-jobs/types'

const job = { id: 'job-a', name: 'Muhasebe', agentId: 'agent-a', agentName: 'Ofis', enabled: true, configuration: { engine: 'mssql', database: 'ERP', host: 'sql', port: null, integratedSecurity: true, databaseCredentialReference: null }, overlapPolicy: 'Skip' } as unknown as BackupJob
const permissions = { canOperate: true, canAdmin: true }
vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => ({ tenantId: 'tenant-a', ...permissions }) }))
vi.mock('@/features/backup-jobs/api', () => ({ getBackupJobs: vi.fn(), getBackupJob: vi.fn(), getOccurrences: vi.fn(), saveBackupJob: vi.fn(), setJobEnabled: vi.fn(), startJobRun: vi.fn() }))

function mount() {
  const cache = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(<QueryClientProvider client={cache}><MemoryRouter><BackupJobsPage /></MemoryRouter></QueryClientProvider>)
}

beforeEach(() => {
  vi.clearAllMocks()
  permissions.canOperate = true
  vi.mocked(getBackupJobs).mockResolvedValue([job])
})

it('a failed load says what happened, what it means for the data and what to do, with the code collapsed and copyable', async () => {
  const writeText = vi.fn().mockResolvedValue(undefined)
  Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true })
  vi.mocked(getBackupJobs).mockRejectedValue(new ApiError(503, { error: 'storage_unavailable' }))
  mount()
  const alert = await screen.findByRole('alert')
  expect(alert).toHaveTextContent('Sunucu isteği tamamlayamadı')
  expect(alert).toHaveTextContent('Mevcut yedekleriniz etkilenmedi')
  expect(alert).toHaveTextContent('Sorun sürerse teknik ayrıntıları destek ekibine iletin.')
  expect(within(alert).getByText('storage_unavailable')).not.toBeVisible()
  await userEvent.click(within(alert).getByText('Teknik ayrıntılar'))
  expect(within(alert).getByText('storage_unavailable')).toBeVisible()
  expect(within(alert).getByText('503')).toBeVisible()
  await userEvent.click(within(alert).getByRole('button', { name: 'Kopyala: Hata kodu' }))
  expect(writeText).toHaveBeenCalledWith('storage_unavailable')
})

it('a refused backup start explains that stored backups are safe and keeps the code under technical details', async () => {
  vi.mocked(startJobRun).mockRejectedValue(new ApiError(409, { error: 'admission_denied', code: 'quota_exceeded' }))
  mount()
  fireEvent.click(await screen.findByRole('button', { name: 'Şimdi çalıştır' }), { detail: 1 })
  const alert = await screen.findByRole('alert')
  expect(alert).toHaveTextContent('Mevcut yedekleriniz silinmedi ve geri yüklenebilir.')
  expect(alert).toHaveTextContent('Depolama alanı açmak için yöneticinize danışın.')
  expect(within(alert).getByText('quota_exceeded')).not.toBeVisible()
})

it('an empty list offers the one primary action to someone who can create jobs', async () => {
  vi.mocked(getBackupJobs).mockResolvedValue([])
  mount()
  expect(await screen.findByText('Henüz yedekleme işi oluşturulmadı.')).toBeInTheDocument()
  expect(screen.getAllByRole('link', { name: /İlk yedeğinizi oluşturun/ })).toHaveLength(1)
})

it('an empty list sends someone without permission to their administrator instead of a missing button', async () => {
  permissions.canOperate = false
  vi.mocked(getBackupJobs).mockResolvedValue([])
  mount()
  expect(await screen.findByText('Henüz yedekleme işi oluşturulmadı.')).toBeInTheDocument()
  expect(screen.getByText('Yöneticinize danışın')).toBeInTheDocument()
  expect(screen.queryByRole('link', { name: /İlk yedeğinizi oluşturun/ })).not.toBeInTheDocument()
})
