import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { render } from '@/test/render'
import { searchFor } from '@/test/search'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { LocationProbe } from '@/features/platform/location-probe'
import { setViewport } from '@/test/viewport'
import { beforeEach, expect, it, vi } from 'vitest'
import { BackupJobsPage } from './backup-jobs-page'
import { getBackupJobs, getHealthTimeline } from '@/features/backup-jobs/api'
import { healthyJobHealth, savedJob } from '@/features/backup-jobs/test-fixtures'
import { getAgents } from '@/features/agents/api'
import { getBackup, getBackups } from '@/features/backups/api'
import { getHealthSummary } from '@/features/dashboard/api'
import { makeRun } from '@/features/backups/test-data'
import { ApiError } from '@/lib/api/client'
import type { Agent } from '@/features/agents/types'
import type { BackupJob } from '@/features/backup-jobs/types'
import type { BackupJobHealth } from '@/features/backup-jobs/health'

vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => ({ tenantId: 'tenant-a' }) }))
vi.mock('@/features/backup-jobs/api')
vi.mock('@/features/agents/api')
vi.mock('@/features/backups/api')
vi.mock('@/features/dashboard/api')

const critical: BackupJobHealth = { ...healthyJobHealth, state: 'Critical', factors: { ...healthyJobHealth.factors, freshness: { ...healthyJobHealth.factors.freshness, score: 0, reason: 'backup_stale' } } }
const schedule = { ...savedJob.schedule!, enabled: true }

/** A job with its own id, name and creation day (2026-10-<day>); everything else is a healthy scheduled job on the computer "Ofis". */
function makeJob(id: string, name: string, day: number, patch: Partial<BackupJob> = {}): BackupJob {
  return { ...savedJob, id, name, createdAt: `2026-10-${String(day).padStart(2, '0')}T08:00:00Z`, schedule: { ...schedule, nextOccurrenceAt: `2026-12-${String(day).padStart(2, '0')}T02:00:00Z` }, ...patch } as BackupJob
}
const onComputer = (agentId: string, agentName: string) => ({ agentId, agentName })
const onDatabase = (database: string, engine: 'mssql' | 'postgres' = 'mssql') => ({ configuration: { ...savedJob.configuration, database, engine } }) as Partial<BackupJob>

let health: BackupJobHealth[]
let succeeded: Record<string, string | undefined>
/** Which jobs have never succeeded: the others report their newest success from `succeeded`. */
let neverSucceeded: string[]

beforeEach(() => {
  vi.clearAllMocks()
  health = []
  succeeded = {}
  neverSucceeded = []
  vi.mocked(getBackupJobs).mockResolvedValue([makeJob('job-a', 'Muhasebe', 1), makeJob('job-b', 'Satış', 2), makeJob('job-c', 'Depo', 3)])
  vi.mocked(getHealthSummary).mockImplementation(async () => ({ totalJobs: health.length, state: 'Healthy', jobs: health }))
  vi.mocked(getHealthTimeline).mockResolvedValue({ jobs: [] })
  vi.mocked(getAgents).mockResolvedValue([{ agentId: 'agent-a', connectivity: 'online' }] as Agent[])
  vi.mocked(getBackups).mockImplementation(async (_tenant, filters) => {
    const id = filters?.jobId ?? ''
    const never = neverSucceeded.includes(id)
    return { items: never ? [] : [makeRun({ id: `run-${id}`, jobId: id, status: 'Succeeded', completedAt: succeeded[id] ?? null })], totalCount: never ? 0 : 1, page: 1, pageSize: 1 }
  })
  vi.mocked(getBackup).mockResolvedValue(makeRun({ id: 'run-r' }))
})

function mount(url = '/tenants/tenant-a/backup-jobs') {
  const cache = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const router = createMemoryRouter([
    { path: '/tenants/:tenantId/backup-jobs', element: <><BackupJobsPage /><LocationProbe /></> },
    { path: '/tenants/:tenantId/backup-jobs/:id', element: <><p>job detail page</p><LocationProbe /></> },
  ], { initialEntries: [url] })
  return render(<QueryClientProvider client={cache}><RouterProvider router={router} /></QueryClientProvider>)
}

const names = () => screen.getAllByRole('row').slice(1).map(row => within(row).getAllByRole('cell')[0].textContent)
const location = () => screen.getByTestId('location')
const header = (name: string) => screen.getByRole('columnheader', { name })
const filter = (name: string) => screen.getByRole('combobox', { name })

async function ready() { await screen.findByRole('table') }

it('shows the jobs in a table with name, computer, database, status, last and next backup and the creation date', async () => {
  succeeded = { 'job-a': '2026-11-19T10:00:00Z' }
  mount()
  await ready()
  expect(screen.getAllByRole('columnheader').map(item => item.textContent)).toEqual(['Ad', 'Durum', 'Bilgisayar', 'Veritabanı', 'Son yedek', 'Sonraki yedek', 'Oluşturulma'])
  const row = screen.getByRole('row', { name: /Muhasebe/ })
  const cells = within(row).getAllByRole('cell')
  expect(within(cells[0]).getByRole('link', { name: 'Muhasebe' })).toHaveAttribute('href', '/tenants/tenant-a/backup-jobs/job-a')
  expect(cells[1]).toHaveTextContent('Güncel')
  expect(cells[2]).toHaveTextContent('Ofis')
  expect(cells[3]).toHaveTextContent('ERP')
  expect(cells[4].querySelector('time')).toHaveAttribute('datetime', '2026-11-19T10:00:00Z')
  expect(cells[5].querySelector('time')).toHaveAttribute('datetime', '2026-12-01T02:00:00Z')
  expect(cells[6].querySelector('time')).toHaveAttribute('datetime', '2026-10-01T08:00:00Z')
})

it('opens a job on its own page', async () => {
  mount()
  await ready()
  fireEvent.click(screen.getByRole('link', { name: 'Satış' }))
  expect(await screen.findByText('job detail page')).toBeInTheDocument()
  expect(location()).toHaveTextContent(/^\/tenants\/tenant-a\/backup-jobs\/job-b$/)
})

it('sorts by the creation date, newest on top, until another order is chosen', async () => {
  mount()
  await ready()
  expect(names()).toEqual(['Depo', 'Satış', 'Muhasebe'])
  expect(header('Oluşturulma')).toHaveAttribute('aria-sort', 'descending')
  expect(header('Ad')).toHaveAttribute('aria-sort', 'none')
  expect(location()).toHaveTextContent(/^\/tenants\/tenant-a\/backup-jobs$/)
})

it('a job created later is at the top, however many jobs there are', async () => {
  const many = Array.from({ length: 30 }, (_, index) => makeJob(`old-${index}`, `Eski ${index}`, 1 + (index % 20)))
  vi.mocked(getBackupJobs).mockResolvedValue([...many, makeJob('fresh', 'Yeni iş', 28)])
  mount()
  await ready()
  expect(names()[0]).toBe('Yeni iş')
})

it('sorts by a column header: first click one way, second the other way, the choice lives in the URL', async () => {
  mount()
  await ready()
  fireEvent.click(within(header('Ad')).getByRole('button'))
  expect(names()).toEqual(['Depo', 'Muhasebe', 'Satış'])
  expect(header('Ad')).toHaveAttribute('aria-sort', 'ascending')
  expect(location()).toHaveTextContent('sort=name&dir=asc')
  fireEvent.click(within(header('Ad')).getByRole('button'))
  expect(names()).toEqual(['Satış', 'Muhasebe', 'Depo'])
  expect(header('Ad')).toHaveAttribute('aria-sort', 'descending')
  expect(location()).toHaveTextContent('sort=name&dir=desc')
  fireEvent.click(within(header('Oluşturulma')).getByRole('button'))
  expect(names()).toEqual(['Depo', 'Satış', 'Muhasebe'])
  expect(location()).toHaveTextContent(/^\/tenants\/tenant-a\/backup-jobs$/)
})

it('sorts by the computer, restoring the order from the URL', async () => {
  vi.mocked(getBackupJobs).mockResolvedValue([makeJob('job-a', 'A', 1, onComputer('c1', 'Zeta')), makeJob('job-b', 'B', 2, onComputer('c2', 'Alfa')), makeJob('job-c', 'C', 3, onComputer('c3', 'Mim'))])
  mount('/tenants/tenant-a/backup-jobs?sort=agent&dir=desc')
  await ready()
  expect(names()).toEqual(['A', 'C', 'B'])
  expect(header('Bilgisayar')).toHaveAttribute('aria-sort', 'descending')
})

it('sorts by the last backup, newest first on the first click', async () => {
  succeeded = { 'job-a': '2026-11-10T00:00:00Z', 'job-b': '2026-11-19T00:00:00Z', 'job-c': '2026-11-01T00:00:00Z' }
  mount()
  await ready()
  fireEvent.click(within(header('Son yedek')).getByRole('button'))
  await waitFor(() => expect(names()).toEqual(['Satış', 'Muhasebe', 'Depo']))
  fireEvent.click(within(header('Son yedek')).getByRole('button'))
  expect(names()).toEqual(['Depo', 'Muhasebe', 'Satış'])
})

it('sorts by the next backup, soonest first on the first click', async () => {
  vi.mocked(getBackupJobs).mockResolvedValue([makeJob('job-a', 'A', 5), makeJob('job-b', 'B', 2), makeJob('job-c', 'C', 9)])
  mount()
  await ready()
  fireEvent.click(within(header('Sonraki yedek')).getByRole('button'))
  expect(names()).toEqual(['B', 'A', 'C'])
})

it.each(['asc', 'desc'])('puts jobs without a last or next backup at the end, sorted %s', async direction => {
  vi.mocked(getBackupJobs).mockResolvedValue([
    makeJob('job-a', 'A', 1), makeJob('job-b', 'B', 2, { schedule: null }), makeJob('job-c', 'C', 3), makeJob('job-d', 'D', 4, { enabled: false }),
  ])
  neverSucceeded = ['job-a', 'job-d']
  succeeded = { 'job-b': '2026-11-02T00:00:00Z', 'job-c': '2026-11-03T00:00:00Z' }
  mount(`/tenants/tenant-a/backup-jobs?sort=next&dir=${direction}`)
  await ready()
  expect(names().slice(0, 2)).toEqual(direction === 'asc' ? ['A', 'C'] : ['C', 'A'])
  expect(names().slice(2).toSorted()).toEqual(['B', 'D'])
  await waitFor(() => expect(location()).toHaveTextContent('sort=next'))
  fireEvent.click(within(header('Son yedek')).getByRole('button'))
  await waitFor(() => expect(names().slice(0, 2)).toEqual(['C', 'B']))
  expect(names().slice(2)).toEqual(['A', 'D'])
  fireEvent.click(within(header('Son yedek')).getByRole('button'))
  expect(names().slice(0, 2)).toEqual(['B', 'C'])
  expect(names().slice(2)).toEqual(['A', 'D'])
})

it('ignores an unknown sort in the URL', async () => {
  mount('/tenants/tenant-a/backup-jobs?sort=nonsense&dir=sideways')
  await ready()
  expect(names()).toEqual(['Depo', 'Satış', 'Muhasebe'])
})

it.each([
  ['name', 'satış', ['Satış']],
  ['computer', 'depo-pc', ['Depo']],
  ['database', 'crm', ['Satış']],
])('searches by %s', async (_what, term, expected) => {
  vi.mocked(getBackupJobs).mockResolvedValue([
    makeJob('job-a', 'Muhasebe', 1), makeJob('job-b', 'Satış', 2, onDatabase('CRM')), makeJob('job-c', 'Depo', 3, onComputer('agent-c', 'Depo-PC')),
  ])
  mount()
  await ready()
  searchFor(screen.getByRole('searchbox', { name: 'İş, yedekleme bilgisayarı veya veritabanı ara' }), term)
  expect(names()).toEqual(expected)
  expect(location()).toHaveTextContent(`search=${encodeURIComponent(term)}`)
})

const statusJobs = () => [
  makeJob('job-current', 'Güncel iş', 1), makeJob('job-attention', 'Dikkatli iş', 2), makeJob('job-first', 'Yeni iş', 3),
  makeJob('job-running', 'Koşan iş', 4, { lastRun: { id: 'run-r', status: 'Running', createdAt: '', startedAt: null, completedAt: null, reasonCode: null } }),
  makeJob('job-paused', 'Duran iş', 5, { enabled: false }), makeJob('job-manual', 'Elle iş', 6, { schedule: null }),
]

it.each([
  ['Güncel', ['Güncel iş']],
  ['Dikkat gerekiyor', ['Dikkatli iş']],
  ['İlk yedek bekleniyor', ['Yeni iş']],
  ['Çalışıyor', ['Koşan iş']],
  ['Elle çalışır', ['Elle iş']],
  ['Pasif', ['Duran iş']],
])('filters by the status "%s", the way the status column says it', async (label, expected) => {
  vi.mocked(getBackupJobs).mockResolvedValue(statusJobs())
  health = [{ ...critical, jobId: 'job-attention' }]
  neverSucceeded = ['job-first']
  succeeded = { 'job-manual': '2026-11-01T00:00:00Z' }
  mount()
  await ready()
  await waitFor(() => expect(within(screen.getByRole('row', { name: /Dikkatli iş/ })).getAllByRole('cell')[1]).toHaveTextContent('Dikkat gerekiyor'))
  expect([...within(filter('Durum')).getAllByRole('option')].map(option => option.textContent)).toEqual(['Tümü', 'Güncel', 'Dikkat gerekiyor', 'İlk yedek bekleniyor', 'Çalışıyor', 'Elle çalışır', 'Pasif'])
  fireEvent.change(filter('Durum'), { target: { value: [...within(filter('Durum')).getAllByRole('option')].find(option => option.textContent === label)!.getAttribute('value')! } })
  expect(names().toSorted()).toEqual(expected.toSorted())
  for (const row of screen.getAllByRole('row').slice(1)) expect(within(row).getAllByRole('cell')[1]).toHaveTextContent(label === 'Pasif' ? 'Durduruldu' : label)
  expect(location()).toHaveTextContent(/status=/)
})

it('offers the computer filter only with more than one computer, and filters by it', async () => {
  const single = mount()
  await ready()
  expect(screen.queryByRole('combobox', { name: 'Bilgisayar' })).not.toBeInTheDocument()
  single.unmount()
  vi.mocked(getBackupJobs).mockResolvedValue([makeJob('job-a', 'A', 1), makeJob('job-b', 'B', 2, onComputer('agent-b', 'Depo-PC')), makeJob('job-c', 'C', 3)])
  mount()
  await ready()
  fireEvent.change(filter('Bilgisayar'), { target: { value: 'agent-b' } })
  expect(names()).toEqual(['B'])
  expect(location()).toHaveTextContent('agent=agent-b')
})

it('offers the database type filter with the engines that are present', async () => {
  vi.mocked(getBackupJobs).mockResolvedValue([makeJob('job-a', 'A', 1), makeJob('job-b', 'B', 2, onDatabase('crm', 'postgres'))])
  mount()
  await ready()
  expect([...within(filter('Veritabanı türü')).getAllByRole('option')].map(option => option.textContent)).toEqual(['Tümü', 'SQL Server', 'PostgreSQL'])
  fireEvent.change(filter('Veritabanı türü'), { target: { value: 'postgres' } })
  expect(names()).toEqual(['B'])
  expect(location()).toHaveTextContent('engine=postgres')
})

it('has no database type filter when every job uses the same engine', async () => {
  mount()
  await ready()
  expect(screen.queryByRole('combobox', { name: 'Veritabanı türü' })).not.toBeInTheDocument()
})

it('ignores a filter value in the URL that the list does not offer', async () => {
  mount('/tenants/tenant-a/backup-jobs?agent=ghost&engine=oracle&status=nonsense')
  await ready()
  expect(names()).toEqual(['Depo', 'Satış', 'Muhasebe'])
})

const manyJobs = (count: number) => Array.from({ length: count }, (_, index) => makeJob(`job-${index}`, `İş ${String(index).padStart(2, '0')}`, 1 + Math.floor(index / 2), { createdAt: `2026-09-${String(1 + Math.floor(index / 2)).padStart(2, '0')}T${String(index % 2).padStart(2, '0')}:00:00Z` }))

it('shows the count but no page buttons for a list of 25 jobs', async () => {
  vi.mocked(getBackupJobs).mockResolvedValue(manyJobs(25))
  mount()
  await ready()
  expect(screen.getAllByRole('row')).toHaveLength(26)
  const paging = within(screen.getByRole('navigation', { name: 'Yedekleme işi sayfaları' }))
  expect(paging.getByText('25 kayıt')).toBeInTheDocument()
  expect(paging.queryByRole('button')).not.toBeInTheDocument()
})

it('shows 25 jobs a page and the page lives in the URL', async () => {
  vi.mocked(getBackupJobs).mockResolvedValue(manyJobs(60))
  mount()
  await ready()
  const table = screen.getByRole('table')
  const paging = within(screen.getByRole('navigation', { name: 'Yedekleme işi sayfaları' }))
  expect(paging.getByText(/60/)).toHaveTextContent('Sayfa 1 / 3')
  expect(within(table).getAllByRole('row')).toHaveLength(26)
  fireEvent.click(paging.getByRole('button', { name: 'Sonraki' }))
  expect(location()).toHaveTextContent(/page=2/)
  expect(paging.getByText(/60/)).toHaveTextContent('Sayfa 2 / 3')
})

it('a page beyond the last shows the last page', async () => {
  vi.mocked(getBackupJobs).mockResolvedValue(manyJobs(60))
  mount('/tenants/tenant-a/backup-jobs?page=9')
  await ready()
  expect(screen.getAllByRole('row')).toHaveLength(11)
  expect(screen.getByRole('navigation', { name: 'Yedekleme işi sayfaları' })).toHaveTextContent('Sayfa 3 / 3')
})

it('going to page 1 after a search, a filter or another sort', async () => {
  vi.mocked(getBackupJobs).mockResolvedValue(manyJobs(60))
  mount('/tenants/tenant-a/backup-jobs?page=3')
  await ready()
  searchFor(screen.getByRole('searchbox', { name: 'İş, yedekleme bilgisayarı veya veritabanı ara' }), 'İş')
  expect(location()).not.toHaveTextContent('page=')
  expect(screen.getByRole('navigation', { name: 'Yedekleme işi sayfaları' })).toHaveTextContent('Sayfa 1 / 3')
  fireEvent.click(within(screen.getByRole('navigation', { name: 'Yedekleme işi sayfaları' })).getByRole('button', { name: 'Sonraki' }))
  fireEvent.change(filter('Durum'), { target: { value: 'current' } })
  expect(location()).not.toHaveTextContent('page=')
  fireEvent.click(within(screen.getByRole('navigation', { name: 'Yedekleme işi sayfaları' })).getByRole('button', { name: 'Sonraki' }))
  fireEvent.click(within(header('Ad')).getByRole('button'))
  expect(location()).not.toHaveTextContent('page=')
})

const revoked = (id: string, name: string, day: number) => makeJob(id, name, day, { agentRevoked: true, enabled: false, ...onComputer('agent-r', 'Eski bilgisayar') })

it('hides the jobs of revoked computers, and a switch shows them at the end, muted and marked', async () => {
  vi.mocked(getBackupJobs).mockResolvedValue([makeJob('job-a', 'Eski iş', 1), revoked('job-r', 'İptal iş', 9), makeJob('job-b', 'Yeni iş', 5)])
  mount()
  await ready()
  expect(names()).toEqual(['Yeni iş', 'Eski iş'])
  fireEvent.click(screen.getByRole('checkbox', { name: 'İptal edilmiş bilgisayarların işlerini göster' }))
  expect(names()).toEqual(['Yeni iş', 'Eski iş', 'İptal iş'])
  expect(location()).toHaveTextContent('revoked=true')
  const cells = within(screen.getByRole('row', { name: /İptal iş/ })).getAllByRole('cell')
  expect(cells[1]).toHaveTextContent('Bilgisayar iptal edildi')
  expect(cells[2].firstElementChild).toHaveClass('text-text-muted')
  fireEvent.click(within(header('Ad')).getByRole('button'))
  expect(names()).toEqual(['Eski iş', 'Yeni iş', 'İptal iş'])
  fireEvent.click(within(header('Ad')).getByRole('button'))
  expect(names()).toEqual(['Yeni iş', 'Eski iş', 'İptal iş'])
})

it('has no revoked switch when no job belongs to a revoked computer', async () => {
  mount()
  await ready()
  expect(screen.queryByRole('checkbox', { name: 'İptal edilmiş bilgisayarların işlerini göster' })).not.toBeInTheDocument()
})

it('a tenant without jobs sees the one action to create the first', async () => {
  vi.mocked(getBackupJobs).mockResolvedValue([])
  mount()
  expect(await screen.findByText('Henüz yedekleme işi oluşturulmadı.')).toBeInTheDocument()
  expect(screen.getAllByRole('link', { name: 'Yedekleme işi oluştur' })).toHaveLength(1)
  expect(screen.getByRole('link', { name: 'Yedekleme işi oluştur' })).toHaveAttribute('href', '/tenants/tenant-a/backup-jobs/new')
  expect(screen.queryByRole('searchbox')).not.toBeInTheDocument()
})

it('says so when every job belongs to a revoked computer, and the switch shows them', async () => {
  vi.mocked(getBackupJobs).mockResolvedValue([revoked('job-r', 'İptal iş', 1)])
  mount()
  expect(await screen.findByText('Tüm işler iptal edilmiş bilgisayarlara ait.')).toBeInTheDocument()
  expect(screen.queryByText('Henüz yedekleme işi oluşturulmadı.')).not.toBeInTheDocument()
  fireEvent.click(screen.getByRole('checkbox', { name: 'İptal edilmiş bilgisayarların işlerini göster' }))
  expect(await screen.findByRole('link', { name: 'İptal iş' })).toBeInTheDocument()
})

it('filters that match nothing say so and offer to clear them; the filters are in the URL', async () => {
  mount('/tenants/tenant-a/backup-jobs?search=yok&status=attention&sort=name&dir=asc')
  expect(await screen.findByText('Aramanızla eşleşen iş yok.')).toBeInTheDocument()
  expect(screen.getAllByRole('button', { name: 'Filtreleri temizle' })).toHaveLength(1)
  fireEvent.click(screen.getByRole('button', { name: 'Filtreleri temizle' }))
  expect(location()).toHaveTextContent(/^\/tenants\/tenant-a\/backup-jobs\?sort=name&dir=asc$/)
  expect(await screen.findByRole('link', { name: 'Muhasebe' })).toBeInTheDocument()
})

it('clears all filters from the filter row when some jobs match', async () => {
  mount('/tenants/tenant-a/backup-jobs?search=satış')
  await ready()
  expect(names()).toEqual(['Satış'])
  fireEvent.click(screen.getByRole('button', { name: 'Filtreleri temizle' }))
  expect(names()).toEqual(['Depo', 'Satış', 'Muhasebe'])
  expect(screen.queryByRole('button', { name: 'Filtreleri temizle' })).not.toBeInTheDocument()
})

it('on a narrow screen the jobs are cards and a menu sorts them', async () => {
  setViewport(false)
  mount()
  const sort = await screen.findByRole('combobox', { name: 'Sırala' })
  expect(screen.queryByRole('table')).not.toBeInTheDocument()
  expect(sort).toHaveValue('created:desc')
  const titles = () => screen.getAllByRole('heading', { level: 2 }).map(item => item.textContent)
  expect(titles()).toEqual(['Depo', 'Satış', 'Muhasebe'])
  fireEvent.change(sort, { target: { value: 'name:asc' } })
  expect(titles()).toEqual(['Depo', 'Muhasebe', 'Satış'])
  expect(location()).toHaveTextContent('sort=name&dir=asc')
})

it('a failed load says what happened and keeps the code under technical details', async () => {
  vi.mocked(getBackupJobs).mockRejectedValue(new ApiError(503, { error: 'storage_unavailable' }))
  mount()
  const alert = await screen.findByRole('alert')
  expect(alert).toHaveTextContent('Depolama geçici olarak kullanılamıyor.')
  expect(within(alert).queryByText('storage_unavailable')).toBeNull()
})
