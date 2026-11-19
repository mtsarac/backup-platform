import { act, screen, waitFor, within } from '@testing-library/react'
import { render } from '@/test/render'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { getAgents } from '@/features/agents/api'
import { getBackupJobs, getHealthTimeline, startJobRun } from '@/features/backup-jobs/api'
import type { BackupJobHealth } from '@/features/backup-jobs/health'
import { healthyJobHealth } from '@/features/backup-jobs/test-fixtures'
import type { BackupJob } from '@/features/backup-jobs/types'
import { getBackup, getBackups } from '@/features/backups/api'
import { makeRun } from '@/features/backups/test-data'
import type { BackupRun } from '@/features/backups/types'
import { getDashboard, getHealthSummary } from '@/features/dashboard/api'
import { getRestores } from '@/features/restores/api'
import { getTenantStorageQuota } from '@/features/storage/api'
import type { Agent } from '@/features/agents/types'
import type { TenantQuota } from '@/features/storage/types'
import { DashboardPage } from './dashboard-page'

vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => ({ tenantId: 'tenant-a', tenant: { name: 'Tenant A' } }) }))
vi.mock('@/features/agents/api')
vi.mock('@/features/backup-jobs/api')
vi.mock('@/features/backups/api')
vi.mock('@/features/dashboard/api')
vi.mock('@/features/restores/api')
vi.mock('@/features/storage/api')

const schedule = { enabled: true, cronExpression: '0 2 * * *', timeZone: 'UTC', missedRunGraceSeconds: 1800, overlapPolicy: 'Skip' } as const
const makeJob = (id: string, name: string, patch: Partial<BackupJob> = {}) => ({ id, name, agentId: 'agent-a', agentName: 'Ofis', enabled: true, configuration: { engine: 'mssql', database: `${name}-db` }, schedule, lastRun: null, ...patch }) as unknown as BackupJob
const health = (jobId: string, reason: string | null, patch: Partial<BackupJobHealth> = {}, factor: keyof BackupJobHealth['factors'] = 'freshness'): BackupJobHealth => {
  const base = { ...healthyJobHealth, jobId, jobName: jobId }
  if (!reason) return { ...base, ...patch }
  return { ...base, state: 'Critical', score: 30, ...patch, factors: { ...base.factors, [factor]: { ...base.factors[factor], score: 0, reason } } }
}
const agent = (connectivity: Agent['connectivity'], agentId = 'agent-a') => ({ agentId, connectivity, hostName: agentId }) as Agent
const day = (index: number, status: string, attemptCount = status === 'Succeeded' ? 1 : 0) => ({ date: `2026-11-${String(index + 1).padStart(2, '0')}`, status, attemptCount })
const strip = (...overrides: [number, string, number?][]) => Array.from({ length: 14 }, (_, index) => { const hit = overrides.find(([at]) => at === index); return hit ? day(index, hit[1], hit[2]) : day(index, 'NoBackup') })
const succeeded = strip([13, 'Succeeded'])
const quota = (patch: Partial<TenantQuota> = {}) => ({ limitBytes: null, unlimited: true, usedBytes: 0, reservedBytes: 0, remainingBytes: null, state: 'Unlimited', ...patch }) as TenantQuota
const page = (items: BackupRun[], totalCount = items.length) => ({ items, totalCount, page: 1, pageSize: 1 })

let jobs: BackupJob[]
let healths: BackupJobHealth[]
let days: Record<string, ReturnType<typeof strip>>
let noSuccess: Set<string>

function mount() {
  const cache = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(<QueryClientProvider client={cache}><MemoryRouter><DashboardPage /></MemoryRouter></QueryClientProvider>)
}
const row = async (name: string) => within(await screen.findByRole('region', { name: 'Ofis' })).getByRole('link', { name }).closest('li')!

beforeEach(() => {
  vi.clearAllMocks()
  jobs = [makeJob('job-a', 'Muhasebe')]
  healths = [health('job-a', null)]
  days = { 'job-a': succeeded }
  noSuccess = new Set()
  vi.mocked(getBackupJobs).mockImplementation(async () => jobs)
  vi.mocked(getAgents).mockResolvedValue([agent('online')])
  vi.mocked(getHealthSummary).mockImplementation(async () => ({ totalJobs: jobs.length, state: null, jobs: healths }))
  vi.mocked(getHealthTimeline).mockImplementation(async () => ({ jobs: jobs.map(job => ({ jobId: job.id, days: days[job.id] ?? strip() })) }))
  vi.mocked(getDashboard).mockResolvedValue({ artifacts: { total: 1, sizeBytes: 5_000_000_000, deletePending: 0, deleteFailed: 0 }, recentRuns: [], upcomingJobs: [] })
  vi.mocked(getBackups).mockImplementation(async (_tenant, filters) => {
    if (!filters?.jobId) return page([makeRun({ id: 'run-last', jobName: 'Muhasebe', status: 'Succeeded', completedAt: '2026-11-18T02:00:00Z' })], 1)
    return noSuccess.has(filters.jobId) ? page([], 0) : page([makeRun({ id: `run-${filters.jobId}`, status: 'Succeeded' })], 1)
  })
  vi.mocked(getBackup).mockResolvedValue(makeRun({ id: 'run-r' }))
  vi.mocked(getRestores).mockResolvedValue({ items: [], totalCount: 0, page: 1, pageSize: 1 })
  vi.mocked(getTenantStorageQuota).mockResolvedValue(quota())
  vi.mocked(startJobRun).mockResolvedValue({ id: 'run-new' })
})
afterEach(() => vi.useRealTimers())

describe('rows', () => {
  it('groups rows by computer with its connection badge and lists no row from an archived job', async () => {
    jobs = [makeJob('job-a', 'Muhasebe'), makeJob('job-b', 'Satış', { agentId: 'agent-b', agentName: 'Depo' })]
    healths = [health('job-a', null), health('job-b', null)]
    vi.mocked(getAgents).mockResolvedValue([agent('online'), agent('unknown', 'agent-b')])
    mount()
    const office = await screen.findByRole('region', { name: 'Ofis' })
    expect(within(office).getByText('Çevrimiçi')).toBeVisible()
    expect(within(office).getByText('Muhasebe')).toBeVisible()
    const store = screen.getByRole('region', { name: 'Depo' })
    expect(within(store).getByText('Bağlantı bilinmiyor')).toBeVisible()
    expect(within(store).getByText('Satış')).toBeVisible()
  })

  it.each([
    ['healthy', {}, 'Güncel'],
    ['the backend says Critical', { health: health('job-a', 'backup_stale') }, 'Dikkat gerekiyor'],
    ['the computer is offline', { connectivity: 'offline' as const }, 'Dikkat gerekiyor'],
    ['no success yet, backend Critical', { health: health('job-a', 'backup_missing'), succeeded: false }, 'İlk yedek bekleniyor'],
    ['the first attempt failed', { health: health('job-a', 'backup_missing'), succeeded: false, days: strip([13, 'Failed', 1]) }, 'Dikkat gerekiyor'],
    ['manual', { job: { schedule: null }, health: health('job-a', 'schedule_unavailable') }, 'Elle çalışır'],
    ['paused', { job: { enabled: false }, health: health('job-a', 'backup_stale') }, 'Durduruldu'],
    ['running', { job: { lastRun: { id: 'run-r', status: 'Running', createdAt: '', startedAt: null, completedAt: null, reasonCode: null } } }, 'Çalışıyor %35'],
  ])('says one word for the row: %s', async (_name, setup: { health?: BackupJobHealth; connectivity?: Agent['connectivity']; job?: Partial<BackupJob>; succeeded?: boolean; days?: ReturnType<typeof strip> }, word) => {
    jobs = [makeJob('job-a', 'Muhasebe', setup.job)]
    if (setup.health) healths = [setup.health]
    if (setup.connectivity) vi.mocked(getAgents).mockResolvedValue([agent(setup.connectivity)])
    if (setup.succeeded === false) noSuccess.add('job-a')
    if (setup.days) days['job-a'] = setup.days
    mount()
    expect(await within(await row('Muhasebe')).findByText(word)).toBeVisible()
  })

  it.each([
    ['attention', [health('job-a', 'backup_stale')], 'Dikkat gerekiyor', 'Dikkat gerekiyor'],
    ['nothing yet', [], 'Henüz yedekleme yok', 'Henüz yedekleme yok'],
    ['all waiting for the first backup', [health('job-a', 'backup_missing')], 'İlk yedek bekleniyor', 'İlk yedek bekleniyor'],
    ['current', [health('job-a', null)], 'Hepsi güncel', 'Hepsi güncel'],
  ])('headlines %s', async (name, healthList, badge) => {
    if (name === 'nothing yet') jobs = []
    if (name === 'all waiting for the first backup') noSuccess.add('job-a')
    healths = healthList
    mount()
    const header = (await screen.findByRole('heading', { level: 1, name: 'Ana sayfa' })).closest('header')!
    await waitFor(() => expect(within(header).getByText(badge)).toBeVisible())
    expect(screen.queryByText(/sağlıklı/i)).not.toBeInTheDocument()
  })

  it('counts every row as protected but neutral rows neither as current nor as attention', async () => {
    jobs = [makeJob('job-a', 'Muhasebe'), makeJob('job-b', 'Satış'), makeJob('job-c', 'Arşiv', { schedule: null }), makeJob('job-d', 'Yeni'), makeJob('job-e', 'Eski', { enabled: false })]
    healths = [health('job-a', null), health('job-b', 'backup_stale'), health('job-c', 'schedule_unavailable'), health('job-d', 'backup_missing'), health('job-e', 'backup_stale')]
    days = { 'job-a': succeeded, 'job-b': succeeded, 'job-c': succeeded, 'job-d': strip(), 'job-e': succeeded }
    noSuccess.add('job-d')
    mount()
    expect(await screen.findByText('5 veri korunuyor · 1 güncel · 1 dikkat gerekiyor')).toBeVisible()
  })

  it('counts a running row by its status before the run', async () => {
    jobs = [makeJob('job-a', 'Muhasebe', { lastRun: { id: 'run-r', status: 'Running', createdAt: '', startedAt: null, completedAt: null, reasonCode: null } })]
    mount()
    expect(await screen.findByText('1 veri korunuyor · 1 güncel · 0 dikkat gerekiyor')).toBeVisible()
  })

  it('puts the one-sentence notes on the row and raises no band for them', async () => {
    healths = [health('job-a', 'restore_untested', { state: 'Degraded', score: 80 }, 'restoreVerification')]
    mount()
    expect(await within(await row('Muhasebe')).findByText('Yedek henüz geri yükleme ile denenmedi.')).toBeVisible()
    expect(screen.queryByRole('region', { name: 'Dikkat gerekiyor' })).not.toBeInTheDocument()
  })
})

describe('strip', () => {
  it('shows 14 cells in one focus stop, each with day, word and attempt count, moved by arrow keys', async () => {
    days['job-a'] = strip([0, 'Failed', 2], [1, 'Running', 1], [2, 'NoJob'], [13, 'Succeeded', 1])
    mount()
    const list = await screen.findByRole('list', { name: /Son 14 gün · Muhasebe/ })
    const cells = within(list).getAllByRole('listitem')
    expect(cells).toHaveLength(14)
    expect(cells[0]).toHaveAccessibleName(/alınamadı, 2 deneme/)
    expect(cells[1]).toHaveAccessibleName(/çalışıyor, 1 deneme/)
    expect(cells[2]).toHaveAccessibleName(/iş yoktu/)
    expect(cells[3]).toHaveAccessibleName(/yedek yoktu/)
    expect(cells[13]).toHaveAccessibleName(/alındı, 1 deneme/)
    expect(cells.filter(cell => cell.tabIndex === 0)).toEqual([cells[13]])
    await userEvent.tab()
    // Focus lands on the first control of the page; walk to the strip.
    cells[13].focus()
    await userEvent.keyboard('{ArrowLeft}{ArrowLeft}')
    expect(cells[11]).toHaveFocus()
    await userEvent.keyboard('{Home}')
    expect(cells[0]).toHaveFocus()
    await userEvent.keyboard('{End}')
    expect(cells[13]).toHaveFocus()
  })

  it('reads "Son başarılı" from the newest taken day, or says "14 günden önce"', async () => {
    jobs = [makeJob('job-a', 'Muhasebe'), makeJob('job-b', 'Satış')]
    healths = [health('job-a', null), health('job-b', null)]
    days = { 'job-a': strip([3, 'Succeeded'], [9, 'Succeeded'], [11, 'Failed', 1]), 'job-b': strip() }
    mount()
    expect(await within(await row('Muhasebe')).findByText(/Son başarılı: .*10/)).toBeVisible()
    expect(within(await row('Satış')).getByText('Son başarılı: 14 günden önce')).toBeVisible()
  })

  it('shows only the last successful date on manual and paused rows', async () => {
    jobs = [makeJob('job-a', 'Muhasebe', { schedule: null })]
    healths = [health('job-a', 'schedule_unavailable')]
    mount()
    const item = await row('Muhasebe')
    expect(await within(item).findByText('Elle çalışır')).toBeVisible()
    expect(within(item).getByText(/Son başarılı:/)).toBeVisible()
    expect(within(item).queryByText(/zamanında|dikkat/i)).not.toBeInTheDocument()
  })
})

describe('band', () => {
  it.each([
    ['a missed backup', health('job-a', 'backup_stale'), 'online' as const, 'Şimdi yedekle', 'button'],
    ['a missed backup on an offline computer', health('job-a', 'backup_stale'), 'offline' as const, 'Bilgisayarı kontrol et', 'link'],
    ['an offline computer', health('job-a', null), 'offline' as const, 'Bilgisayarı kontrol et', 'link'],
    ['a nearly full storage', health('job-a', 'capacity_critical', {}, 'storageCapacity'), 'online' as const, 'Depolamayı yönet', 'link'],
    ['a failed trial restore', health('job-a', 'restore_failed', {}, 'restoreVerification'), 'online' as const, 'Yeniden dene', 'link'],
  ])('offers the fitting action for %s', async (_name, jobHealth, connectivity, action, role) => {
    healths = [jobHealth]
    vi.mocked(getAgents).mockResolvedValue([agent(connectivity)])
    mount()
    const band = await screen.findByRole('region', { name: 'Dikkat gerekiyor' })
    expect(within(band).getByRole(role as 'button', { name: action })).toBeVisible()
    expect(within(band).getByRole('link', { name: 'Ayrıntıyı gör' })).toHaveAttribute('href', '/tenants/tenant-a/backup-jobs/job-a')
  })

  it('points the actions at the computer, the storage page and the restore of the latest backup', async () => {
    healths = [health('job-a', 'restore_failed', {}, 'restoreVerification')]
    mount()
    expect(within(await screen.findByRole('region', { name: 'Dikkat gerekiyor' })).getByRole('link', { name: 'Yeniden dene' })).toHaveAttribute('href', '/tenants/tenant-a/backup-runs/run-job-a/restore')
  })

  it.each([
    ['missing replicas', health('job-a', 'replicas_missing', {}, 'replication')],
    ['no verified replica', health('job-a', 'no_verified_replica', {}, 'replication')],
  ])('shows %s without an action', async (_name, jobHealth) => {
    healths = [jobHealth]
    mount()
    const band = await screen.findByRole('region', { name: 'Dikkat gerekiyor' })
    expect(within(band).queryByRole('button')).not.toBeInTheDocument()
    expect(within(band).getAllByRole('link')).toHaveLength(1)
  })

  it('names the most urgent topic and counts the others', async () => {
    jobs = [makeJob('job-a', 'Muhasebe'), makeJob('job-b', 'Satış'), makeJob('job-c', 'Arşiv')]
    healths = [health('job-a', 'restore_failed', {}, 'restoreVerification'), health('job-b', 'backup_stale'), health('job-c', 'capacity_critical', {}, 'storageCapacity')]
    days = { 'job-a': succeeded, 'job-b': succeeded, 'job-c': succeeded }
    mount()
    const band = await screen.findByRole('region', { name: 'Dikkat gerekiyor' })
    expect(band).toHaveTextContent('Satış: yedek zamanında alınmadı ve 2 veri daha')
    expect(within(band).getByRole('link', { name: 'Ayrıntıyı gör' })).toHaveAttribute('href', '/tenants/tenant-a/backup-jobs/job-b')
  })

  it('starts a backup from the band', async () => {
    healths = [health('job-a', 'backup_stale')]
    mount()
    await userEvent.click(await screen.findByRole('button', { name: 'Şimdi yedekle' }))
    await waitFor(() => expect(startJobRun).toHaveBeenCalledWith('tenant-a', 'job-a', expect.any(String)))
    expect(await screen.findByRole('link', { name: 'Denemeyi aç' })).toHaveAttribute('href', '/tenants/tenant-a/backup-runs/run-new')
  })

  it('shows a neutral running band while nothing needs attention', async () => {
    jobs = [makeJob('job-a', 'Muhasebe', { lastRun: { id: 'run-r', status: 'Running', createdAt: '', startedAt: null, completedAt: null, reasonCode: null } })]
    mount()
    expect(await screen.findByRole('region', { name: 'Çalışan yedekleme' })).toHaveTextContent('Muhasebe: yedekleme çalışıyor %35')
  })

  it('shows no band when everything is current', async () => {
    mount()
    await row('Muhasebe')
    expect(screen.queryByRole('region', { name: /Dikkat gerekiyor|Çalışan yedekleme/ })).not.toBeInTheDocument()
  })
})

describe('restore actions', () => {
  it('opens the restore of the newest successful attempt of the row, and the successful filter of the history from the header', async () => {
    mount()
    expect(within(await row('Muhasebe')).getByRole('link', { name: 'Muhasebe için geri yükle' })).toHaveAttribute('href', '/tenants/tenant-a/backup-runs/run-job-a/restore')
    expect(getBackups).toHaveBeenCalledWith('tenant-a', { status: 'Succeeded', jobId: 'job-a', page: 1, pageSize: 1 }, expect.anything())
    expect(screen.getByRole('link', { name: 'Geri yükle' })).toHaveAttribute('href', '/tenants/tenant-a/backup-runs?status=Succeeded')
  })

  it('disables the row button and says why when the job has no successful backup', async () => {
    noSuccess.add('job-a')
    mount()
    const item = await row('Muhasebe')
    expect(within(item).getByRole('button', { name: 'Muhasebe için geri yükle' })).toBeDisabled()
    expect(within(item).getByText('henüz yedek yok')).toBeVisible()
  })
})

describe('cards', () => {
  it('shows the nearest next backup and the latest successful backup from its one filtered call', async () => {
    vi.mocked(getDashboard).mockResolvedValue({ artifacts: { total: 1, sizeBytes: 5_000_000_000, deletePending: 0, deleteFailed: 0 }, recentRuns: [], upcomingJobs: [
      makeJob('job-b', 'Geç', { schedule: { ...schedule, nextOccurrenceAt: '2026-11-21T02:00:00Z' } }),
      makeJob('job-c', 'Erken', { schedule: { ...schedule, nextOccurrenceAt: '2026-11-20T02:00:00Z' } }),
    ] })
    mount()
    const card = (await screen.findByRole('heading', { name: 'Sıradaki ve son yedek' })).closest('section')!
    expect(within(card).getByRole('link', { name: 'Erken' })).toHaveAttribute('href', '/tenants/tenant-a/backup-jobs/job-c')
    expect(within(card).queryByRole('link', { name: 'Geç' })).not.toBeInTheDocument()
    expect(within(card).getByRole('link', { name: 'Muhasebe' })).toHaveAttribute('href', '/tenants/tenant-a/backup-runs/run-last')
    expect(vi.mocked(getBackups).mock.calls.filter(([, filters]) => !filters?.jobId)).toHaveLength(1)
  })

  it('shows the storage total, the quota bar only with a quota, and a link to manage', async () => {
    mount()
    const card = (await screen.findByRole('heading', { name: 'Depolama' })).closest('section')!
    expect(await within(card).findByText('5 GB')).toBeVisible()
    expect(within(card).getByRole('link', { name: 'Yönet' })).toHaveAttribute('href', '/tenants/tenant-a/storage')
    expect(within(card).queryByRole('progressbar')).not.toBeInTheDocument()
  })

  it('draws the quota bar when the plan has a limit', async () => {
    vi.mocked(getTenantStorageQuota).mockResolvedValue(quota({ limitBytes: 10_000_000_000, unlimited: false, usedBytes: 5_000_000_000, state: 'Normal' }))
    mount()
    const card = (await screen.findByRole('heading', { name: 'Depolama' })).closest('section')!
    expect(await within(card).findByRole('progressbar', { name: 'Kota kullanımı' })).toHaveAttribute('aria-valuenow', '50')
    expect(within(card).getByText('Kota: 10 GB')).toBeVisible()
  })

  it('lists four recent runs with one sentence for a failure', async () => {
    const runs = ['1', '2', '3', '4', '5'].map(id => makeRun({ id: `r${id}`, jobName: `İş ${id}`, status: id === '1' ? 'Failed' : 'Succeeded', reasonCode: id === '1' ? 'authentication_failed' : null }))
    vi.mocked(getDashboard).mockResolvedValue({ artifacts: { total: 1, sizeBytes: 1, deletePending: 0, deleteFailed: 0 }, recentRuns: runs, upcomingJobs: [] })
    mount()
    const card = (await screen.findByRole('heading', { name: 'Son etkinlik' })).closest('section')!
    expect(await within(card).findAllByRole('listitem')).toHaveLength(4)
    expect(within(card).queryByText('İş 5')).not.toBeInTheDocument()
    expect(within(card).getByText(/Kimlik doğrulaması başarısız\./)).toBeVisible()
  })
})

describe('no jobs', () => {
  beforeEach(() => { jobs = []; healths = [] })

  it.each([
    ['no computer', [], 0, 0, 'Yedekleme bilgisayarı ekleyin', 0],
    ['a computer', [agent('online')], 0, 0, 'Yedeklenecek veriyi seçin', 1],
    ['a computer and a past successful backup', [agent('online')], 1, 0, 'Yedeklenecek veriyi seçin', 2],
  ])('continues the real progress with %s', async (_name, agents, backups, restores, current, done) => {
    vi.mocked(getAgents).mockResolvedValue(agents)
    vi.mocked(getBackups).mockResolvedValue(page(backups ? [makeRun({ id: 'old', status: 'Succeeded' })] : [], backups))
    vi.mocked(getRestores).mockResolvedValue({ items: [], totalCount: restores, page: 1, pageSize: 1 })
    mount()
    const steps = within(await screen.findByRole('list')).getAllByRole('listitem')
    expect(steps).toHaveLength(4)
    expect(steps.find(step => step.getAttribute('aria-current') === 'step')).toHaveTextContent(current)
    expect(steps.filter(step => within(step).queryByText('Tamamlandı'))).toHaveLength(done)
    if (current !== 'İlk yedeğinizi alın') expect(within(steps.find(step => step.getAttribute('aria-current') === 'step')!).getByRole('link', { name: 'Başla' })).toHaveAttribute('href', '/tenants/tenant-a/first-backup')
  })

  it('shows the empty ghost and the storage card, and hides band, counts, activity and the next backup', async () => {
    mount()
    expect(await screen.findByText('Başlangıç adımları')).toBeVisible()
    expect(screen.getByText('Korunan verileriniz burada görünecek.')).toBeVisible()
    expect(screen.getByRole('heading', { name: 'Depolama' })).toBeVisible()
    expect(screen.getByText('Henüz yedekleme yok')).toBeVisible()
    expect(screen.queryByText(/veri korunuyor/)).not.toBeInTheDocument()
    for (const name of ['Son etkinlik', 'Sıradaki ve son yedek']) expect(screen.queryByRole('heading', { name })).not.toBeInTheDocument()
    expect(screen.queryByRole('region', { name: /Dikkat gerekiyor/ })).not.toBeInTheDocument()
  })
})

it('leaves out the removed panels', async () => {
  mount()
  await row('Muhasebe')
  for (const text of [/24 saat/, /Gözlemlenebilirlik/, /Doğrulanmış kopyalar/, /Geri yüklenebilirlik/, /silinemedi/]) expect(screen.queryByText(text)).not.toBeInTheDocument()
})

it('asks for fresh data every 15 seconds', async () => {
  vi.useFakeTimers({ shouldAdvanceTime: true })
  mount()
  await row('Muhasebe')
  const before = vi.mocked(getBackupJobs).mock.calls.length
  await act(() => vi.advanceTimersByTimeAsync(15_100))
  expect(vi.mocked(getBackupJobs).mock.calls.length).toBeGreaterThan(before)
  expect(vi.mocked(getHealthTimeline).mock.calls.length).toBeGreaterThan(1)
  expect(vi.mocked(getDashboard).mock.calls.length).toBeGreaterThan(1)
})

describe('jobs of a revoked computer', () => {
  const revoked = (id: string, name: string) => makeJob(id, name, { agentId: 'agent-r', agentName: 'Eski bilgisayar', enabled: false, agentRevoked: true, schedule: { ...schedule, nextOccurrenceAt: '2026-11-20T02:00:00Z' } })

  it('keeps them out of the counts and the badge and lists them in their own quiet group at the bottom', async () => {
    jobs = [makeJob('job-a', 'Muhasebe'), revoked('job-r', 'Eski iş')]
    healths = [health('job-a', null), health('job-r', 'backup_stale')]
    mount()
    expect(await screen.findByText('1 veri korunuyor · 1 güncel · 0 dikkat gerekiyor')).toBeVisible()
    expect(screen.getByText('Hepsi güncel')).toBeVisible()
    expect(screen.queryByRole('region', { name: /Dikkat gerekiyor/ })).not.toBeInTheDocument()
    expect(screen.queryByRole('region', { name: 'Eski bilgisayar' })).not.toBeInTheDocument()
    const group = screen.getByRole('region', { name: 'Bilgisayarı iptal edilmiş işler' })
    expect(within(group).getByRole('link', { name: 'Eski iş' })).toHaveAttribute('href', '/tenants/tenant-a/backup-jobs/job-r')
    expect(within(group).getByText(/Eski bilgisayar/)).toBeVisible()
    expect(within(group).getByText('Bilgisayar iptal edildi')).toBeVisible()
    expect(within(group).queryByRole('list', { name: /Son 14 gün/ })).not.toBeInTheDocument()
    const regions = screen.getAllByRole('region')
    expect(regions.indexOf(group)).toBeGreaterThan(regions.indexOf(screen.getByRole('region', { name: 'Ofis' })))
  })

  it('never offers a job of a revoked computer as the next backup', async () => {
    jobs = [makeJob('job-a', 'Muhasebe'), revoked('job-r', 'Eski iş')]
    healths = [health('job-a', null), health('job-r', null)]
    vi.mocked(getDashboard).mockResolvedValue({ artifacts: { total: 1, sizeBytes: 1, deletePending: 0, deleteFailed: 0 }, recentRuns: [], upcomingJobs: [jobs[1]] })
    mount()
    const card = (await screen.findByRole('heading', { name: 'Sıradaki ve son yedek' })).closest('section')!
    expect(within(card).queryByRole('link', { name: 'Eski iş' })).not.toBeInTheDocument()
    expect(within(card).getByText('Etkin zamanlama yok.')).toBeVisible()
  })

  it('says there is nothing protected when only revoked computers remain', async () => {
    jobs = [revoked('job-r', 'Eski iş')]
    healths = [health('job-r', null)]
    mount()
    expect(await screen.findByText('Korunan verileriniz burada görünecek.')).toBeVisible()
    expect(screen.queryByText('Hepsi güncel')).not.toBeInTheDocument()
    expect(within(screen.getByRole('region', { name: 'Bilgisayarı iptal edilmiş işler' })).getByRole('link', { name: 'Eski iş' })).toBeVisible()
  })
})
