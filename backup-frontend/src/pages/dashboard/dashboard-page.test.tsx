import { screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { render } from '@/test/render'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { setFeatureFlags } from '@/features/feature-flags/feature-flags'
import { changeLanguage } from '@/lib/i18n/runtime'
import { setViewport } from '@/test/viewport'
import { makeRun } from '@/features/backups/test-data'
import type { BackupRun } from '@/features/backups/types'
import { DashboardPage } from './dashboard-page'

type Factor = { score: number; maxScore: number; status: string; reason: string }
const goodFactors = {
  freshness: { score: 40, maxScore: 40, status: 'Available', reason: 'backup_fresh' } as Factor,
  replication: { score: 30, maxScore: 30, status: 'Available', reason: 'replicas_verified' } as Factor,
  restoreVerification: { score: 20, maxScore: 20, status: 'Passed', reason: 'restore_passed' } as Factor,
  storageCapacity: { score: 10, maxScore: 10, status: 'Available', reason: 'capacity_available' } as Factor,
}
const jobHealth = (jobId: string, jobName: string, state: 'Healthy' | 'Degraded' | 'Critical', score: number, factors: Partial<typeof goodFactors> = {}) =>
  ({ jobId, jobName, score, state, evaluatedAt: '2026-10-21T12:00:00Z', factors: { ...goodFactors, ...factors } })

const emptyObservability = () => ({
  totalJobs: 0, averageScore: null as number | null, state: null as string | null, healthy: 0, degraded: 0, critical: 0,
  jobs: [] as ReturnType<typeof jobHealth>[],
  capacityRisks: [] as { connectionId: string; connectionName: string; status: string; totalBytes: number; usedBytes: number; freeBytes: number; reportedAt: string }[],
  recentSizeAnomalies: [] as { runId: string; jobId: string; jobName: string; completedAt: string; sizeBytes: number; baselineMeanBytes: number; deviationRatio: number }[],
  recentSizeAnomaliesTotal: 0,
})

const state = vi.hoisted(() => ({
  canAdmin: true,
  canOperate: true,
  data: {
    jobs: { total: 1, enabled: 1, scheduled: 0 },
    agents: { total: 1, online: 1, offline: 0, unknown: 0, disabled: 0, revoked: 0 },
    runs: { active: 0, running: 0, retryPending: 0, cancellationRequested: 0, storageFailedLast24Hours: 0, failedLast24Hours: 0, succeededLast24Hours: 1, replicationInsufficientLast24Hours: undefined as number | undefined },
    artifacts: { total: 1, sizeBytes: 100, deletePending: 0, deleteFailed: 0 },
    recentRuns: [] as BackupRun[],
    upcomingJobs: [],
  },
  observability: {} as ReturnType<typeof emptyObservability>,
  pending: false,
  error: null as Error | null,
}))
vi.mock('@tanstack/react-query', () => ({ useQueryClient: () => ({ refetchQueries: vi.fn() }), useQuery: ({ queryKey }: { queryKey: string[] }) => ({ data: state.pending || state.error ? undefined : queryKey.includes('observability') ? state.observability : state.data, isPending: state.pending, error: state.error, refetch: vi.fn() }) }))
vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => ({ tenantId: 'tenant-a', tenant: { name: 'Tenant A' }, canAdmin: state.canAdmin, canOperate: state.canOperate }) }))

function renderDashboard() {
  return render(<MemoryRouter><DashboardPage /></MemoryRouter>)
}
const openDetails = () => userEvent.click(screen.getByText('Ayrıntılar'))

beforeEach(() => {
  setFeatureFlags({ sizeAnomaly: true })
  localStorage.clear()
  state.canAdmin = true
  state.canOperate = true
  state.pending = false
  state.error = null as Error | null
  state.data.jobs = { total: 1, enabled: 1, scheduled: 0 }
  state.data.agents.offline = 0
  state.data.runs.failedLast24Hours = 0
  state.data.runs.succeededLast24Hours = 1
  state.data.runs.storageFailedLast24Hours = 0
  state.data.artifacts.deleteFailed = 0
  state.data.runs.replicationInsufficientLast24Hours = undefined
  state.data.recentRuns = []
  state.observability = { ...emptyObservability(), totalJobs: 1, state: 'Healthy', healthy: 1, averageScore: 100, jobs: [jobHealth('job-1', 'Muhasebe', 'Healthy', 100)] }
})

describe('tenant status card', () => {
  it.each([
    ['Healthy', 'Güvende'],
    ['Degraded', 'Dikkat'],
    ['Critical', 'Acil'],
  ])('shows the backend state %s as "%s"', (backendState, word) => {
    state.observability = { ...state.observability, state: backendState }
    renderDashboard()
    expect(within(screen.getByRole('status')).getByText(word)).toBeVisible()
  })

  it('never recomputes the state from the jobs or the average score', () => {
    state.observability = { ...state.observability, state: 'Healthy', averageScore: 10, critical: 1, jobs: [jobHealth('job-1', 'Muhasebe', 'Critical', 10)] }
    renderDashboard()
    expect(within(screen.getByRole('status')).getByText('Güvende')).toBeVisible()
    expect(within(screen.getByRole('status')).queryByText('Acil')).not.toBeInTheDocument()
  })

  it('shows a neutral card that opens the job wizard when the tenant has no jobs', () => {
    state.observability = emptyObservability()
    renderDashboard()
    expect(screen.getByText('Henüz yedekleme kurulmadı')).toBeVisible()
    expect(screen.getByRole('link', { name: 'İlk yedeğinizi oluşturun' })).toHaveAttribute('href', '/tenants/tenant-a/backup-jobs/new')
    for (const word of ['Güvende', 'Dikkat', 'Acil']) expect(screen.queryByText(word)).not.toBeInTheDocument()
  })

  it('offers the create action to an operator who is not an administrator', () => {
    state.canAdmin = false
    renderDashboard()
    expect(screen.getByRole('link', { name: 'Yeni yedekleme işi' })).toHaveAttribute('href', '/tenants/tenant-a/backup-jobs/new')
  })

  it('offers the first-backup action to an operator when the tenant has no jobs, as the only create link', () => {
    state.canAdmin = false
    state.observability = emptyObservability()
    renderDashboard()
    expect(screen.getByRole('link', { name: 'İlk yedeğinizi oluşturun' })).toBeVisible()
    expect(screen.queryByRole('link', { name: 'Yeni yedekleme işi' })).not.toBeInTheDocument()
  })

  it('sends a viewer to their administrator instead of offering any create action', () => {
    state.canAdmin = false
    state.canOperate = false
    renderDashboard()
    expect(screen.queryByRole('link', { name: 'Yeni yedekleme işi' })).not.toBeInTheDocument()
    state.observability = emptyObservability()
    renderDashboard()
    expect(screen.queryByRole('link', { name: 'İlk yedeğinizi oluşturun' })).not.toBeInTheDocument()
    expect(screen.getByText('Yöneticinize danışın')).toBeVisible()
  })

  it('names the page like the navigation entry', () => {
    renderDashboard()
    expect(screen.getByRole('heading', { level: 1, name: 'Genel Bakış' })).toBeVisible()
  })

  it('has no cards/table view switch and stores no view preference', () => {
    renderDashboard()
    expect(screen.queryByRole('button', { name: /kart|tablo/i })).not.toBeInTheDocument()
    expect(localStorage.length).toBe(0)
  })
})

describe('tenant recommendations', () => {
  it('turns each unhealthy job\'s weakest factor into a link to that job, most urgent first', () => {
    state.observability = { ...state.observability, state: 'Critical', jobs: [
      jobHealth('job-ok', 'Sağlam yedek', 'Healthy', 100),
      jobHealth('job-warn', 'Depo işi', 'Degraded', 75, { storageCapacity: { score: 5, maxScore: 10, status: 'Warning', reason: 'capacity_warning' } }),
      jobHealth('job-bad', 'Muhasebe', 'Critical', 30, { freshness: { score: 0, maxScore: 40, status: 'Unavailable', reason: 'backup_missing' } }),
    ] }
    renderDashboard()
    const items = within(screen.getByRole('list', { name: 'Öneriler' })).getAllByRole('listitem')
    expect(items).toHaveLength(2)
    expect(within(items[0]).getByRole('link')).toHaveTextContent('Muhasebe: Henüz başarılı yedek alınmadı')
    expect(within(items[0]).getByRole('link')).toHaveAttribute('href', '/tenants/tenant-a/backup-jobs/job-bad/edit')
    expect(within(items[1]).getByRole('link')).toHaveTextContent('Depo işi: Depolama alanı doluyor')
    expect(within(items[1]).getByRole('link')).toHaveAttribute('href', '/tenants/tenant-a/backup-jobs/job-warn/edit')
  })

  it('shows at most three recommendations', () => {
    const stale = { freshness: { score: 0, maxScore: 40, status: 'Degraded', reason: 'backup_stale' } }
    state.observability = { ...state.observability, state: 'Degraded', jobs: ['a', 'b', 'c', 'd', 'e'].map((id, index) => jobHealth(id, `İş ${id}`, 'Degraded', 60 + index, stale)) }
    renderDashboard()
    expect(within(screen.getByRole('list', { name: 'Öneriler' })).getAllByRole('link')).toHaveLength(3)
  })

  it('has no recommendations list when every job is healthy', () => {
    renderDashboard()
    expect(screen.queryByRole('list', { name: 'Öneriler' })).not.toBeInTheDocument()
  })

  it('still links an unhealthy job whose reason code is not known', () => {
    state.observability = { ...state.observability, state: 'Degraded', jobs: [jobHealth('job-x', 'Yeni etken', 'Degraded', 70, { freshness: { score: 10, maxScore: 40, status: 'Degraded', reason: 'brand_new_code' } })] }
    renderDashboard()
    expect(within(screen.getByRole('list', { name: 'Öneriler' })).getByRole('link', { name: /Yeni etken/ })).toHaveAttribute('href', '/tenants/tenant-a/backup-jobs/job-x/edit')
  })
})

describe('dashboard loading and failure', () => {
  it('shows a loading status while the data arrives and no stale verdict', () => {
    state.pending = true
    renderDashboard()
    expect(screen.getAllByRole('status', { name: 'Yükleniyor' }).length).toBeGreaterThan(0)
    expect(screen.queryByText('Yedekleriniz güvende')).not.toBeInTheDocument()
  })

  it('offers a retry when the data could not be loaded', () => {
    state.error = new Error('network')
    renderDashboard()
    expect(screen.getAllByRole('button', { name: 'Tekrar dene' }).length).toBeGreaterThan(0)
  })

  it.each([['Degraded', 'Yedeklerinizden bazıları dikkat istiyor'], ['Critical', 'Yedeklerinizde acil bir sorun var']])('tells the user to open the listed jobs when the state is %s', (backendState, title) => {
    state.observability = { ...state.observability, state: backendState, jobs: [jobHealth('job-bad', 'Muhasebe', 'Critical', 30, { freshness: { score: 0, maxScore: 40, status: 'Unavailable', reason: 'backup_missing' } })] }
    renderDashboard()
    expect(screen.getByText(title)).toBeVisible()
    expect(within(screen.getByRole('list', { name: 'Öneriler' })).getByRole('link', { name: /Muhasebe/ })).toHaveAttribute('href', '/tenants/tenant-a/backup-jobs/job-bad/edit')
  })
})

describe('dashboard information ownership', () => {
  it('lists each unhealthy job once: the top three above, only the rest in the details', async () => {
    const stale = { freshness: { score: 0, maxScore: 40, status: 'Degraded', reason: 'backup_stale' } }
    state.observability = { ...state.observability, state: 'Degraded', degraded: 5, jobs: ['a', 'b', 'c', 'd', 'e'].map((id, index) => jobHealth(id, `İş ${id}`, 'Degraded', 60 + index, stale)) }
    renderDashboard()
    await openDetails()
    for (const id of ['a', 'b', 'c', 'd', 'e']) expect(screen.getAllByRole('link', { name: new RegExp(`İş ${id}`) })).toHaveLength(1)
    expect(within(screen.getByRole('list', { name: 'Öneriler' })).getAllByRole('link')).toHaveLength(3)
  })

  it('shows a failure total with its storage cause split and one link, not a second alert', () => {
    state.data.runs.failedLast24Hours = 8
    state.data.runs.storageFailedLast24Hours = 6
    renderDashboard()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    expect(screen.getByText(/6 tanesi depolamaya yazma veya kopya doğrulama hatası/)).toBeVisible()
    expect(screen.getAllByRole('link', { name: 'Başarısız yedeklemeler' })).toHaveLength(1)
    expect(screen.getByRole('link', { name: 'Başarısız yedeklemeler' })).toHaveAttribute('href', '/tenants/tenant-a/backup-runs?status=Failed')
  })

  it('has one link to the run history, in the recent-runs section', () => {
    renderDashboard()
    expect(screen.getAllByRole('link', { name: /Yedekleme denemeleri|Tüm geçmiş/ })).toHaveLength(1)
  })

  it('hides the health breakdown for a tenant without jobs', async () => {
    state.observability = emptyObservability()
    renderDashboard()
    await openDetails()
    expect(screen.queryByText('Ortalama sağlık puanı')).not.toBeInTheDocument()
    expect(screen.queryByText('Sağlık değerlendirmesi için henüz iş yok.')).not.toBeInTheDocument()
  })
})

describe('dashboard missing-copy ownership', () => {
  it.each([
    ['replicas_missing', 'Yedeğin yeterince kopyası yok'],
    ['no_verified_replica', 'Yedeğin doğrulanmış kopyası yok'],
  ])('groups the %s health recommendation and the last-24-hour aggregate in one owning risk section', async (reason, message) => {
    state.observability = { ...state.observability, state: 'Degraded', jobs: [
      jobHealth('job-copy', 'Muhasebe', 'Degraded', 70, { replication: { score: 0, maxScore: 30, status: 'Unavailable', reason } }),
    ] }
    state.data.runs.replicationInsufficientLast24Hours = 4
    renderDashboard()
    await openDetails()
    const risk = screen.getByRole('region', { name: 'Doğrulanmış kopyalar' })
    expect(within(risk).getByRole('alert')).toHaveTextContent('Son 24 saatte 4 yedekleme')
    expect(within(risk).getByRole('alert')).toHaveTextContent('Yedekler kullanılabilir durumda ama gereken kopya sayısına ulaşılamadı.')
    expect(within(risk).getByRole('link', { name: `Muhasebe: ${message}` })).toHaveAttribute('href', '/tenants/tenant-a/backup-jobs/job-copy/edit')
    expect(within(risk).getByRole('link', { name: 'Çoğaltma sonuçlarını inceleyin.' })).toHaveAttribute('href', '/tenants/tenant-a/backup-runs?status=Succeeded')
    expect(screen.getAllByRole('link', { name: /Muhasebe:/ })).toHaveLength(1)
    expect(screen.getAllByText(/Son 24 saatte 4 yedekleme/)).toHaveLength(1)
    expect(screen.getAllByRole('alert')).toHaveLength(1)
  })

  it('retains historical missing-copy health when the last 24 hours have no shortfalls', () => {
    state.observability = { ...state.observability, state: 'Degraded', jobs: [
      jobHealth('job-copy', 'Eski yedek', 'Degraded', 70, { replication: { score: 0, maxScore: 30, status: 'Unavailable', reason: 'replicas_missing' } }),
    ] }
    state.data.runs.replicationInsufficientLast24Hours = 0
    renderDashboard()
    const risk = screen.getByRole('region', { name: 'Doğrulanmış kopyalar' })
    expect(within(risk).getByRole('link', { name: 'Eski yedek: Yedeğin yeterince kopyası yok' })).toHaveAttribute('href', '/tenants/tenant-a/backup-jobs/job-copy/edit')
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    expect(screen.queryByText(/Son 24 saatte/)).not.toBeInTheDocument()
  })
})

describe('dashboard details', () => {
  it('keeps the counters, observability panels and average score collapsed until opened', async () => {
    state.observability = { ...state.observability, averageScore: 70, capacityRisks: [{ connectionId: 'disk-risk', connectionName: 'Dolu disk', status: 'Critical', totalBytes: 1_000_000_000, usedBytes: 920_000_000, freeBytes: 80_000_000, reportedAt: '2026-10-21T12:00:00Z' }] }
    renderDashboard()
    expect(screen.getByText('Ayrıntılar')).toBeVisible()
    expect(screen.getByText('Dolu disk')).not.toBeVisible()
    expect(screen.getByText('Ortalama sağlık puanı')).not.toBeVisible()
    expect(screen.getByText('Çevrimdışı')).not.toBeVisible()
    await openDetails()
    expect(screen.getByText('Dolu disk')).toBeVisible()
    expect(screen.getByText('Ortalama sağlık puanı')).toBeVisible()
    expect(screen.getByText('Çevrimdışı')).toBeVisible()
  })

  it('shows enabled and scheduled job counts as distinct facts once each', async () => {
    state.data.jobs = { total: 9, enabled: 7, scheduled: 3 }
    renderDashboard()
    expect(screen.getByText('Etkin yedekleme işleri')).not.toBeVisible()
    await openDetails()
    expect(screen.getAllByText('Etkin yedekleme işleri')).toHaveLength(1)
    expect(screen.getByText('Etkin yedekleme işleri').nextElementSibling).toHaveTextContent(/^7$/)
    expect(screen.getAllByText('Planlı yedekleme işleri')).toHaveLength(1)
    expect(screen.getByText('Planlı yedekleme işleri').nextElementSibling).toHaveTextContent(/^3$/)
  })
})

describe('dashboard replication monitoring', () => {
  it('alerts for insufficient replication while retaining the available backup and engine success', async () => {
    state.data.runs.replicationInsufficientLast24Hours = 1
    state.data.recentRuns = [makeRun({ status: 'Succeeded', replication: {
      requiredVerifiedReplicas: 3, verifiedReplicas: 2, destinationCount: 3,
      status: 'InsufficientReplicas', policySatisfied: false, destinations: [],
    } })]
    renderDashboard()
    await openDetails()
    expect(screen.getByRole('alert')).toHaveTextContent('Son 24 saatte 1 yedekleme kullanılabilir yedek oluşturdu, ancak gereken doğrulanmış kopya sayısına ulaşamadı.')
    expect(screen.getByRole('link', { name: "Çoğaltma sonuçlarını inceleyin." })).toHaveAttribute('href', '/tenants/tenant-a/backup-runs?status=Succeeded')
    expect(screen.getAllByText('Başarılı').length).toBeGreaterThan(0)
    expect(screen.getByText("Doğrulanmış kopya sayısı yetersiz")).toBeInTheDocument()
    expect(within(screen.getByText("Yedek oluşturuldu · son 24 saat").parentElement!.parentElement!).getByText('1')).toBeInTheDocument()
  })

  it('alerts from the tenant-wide count even when the insufficient run is outside recent history', async () => {
    state.data.runs.replicationInsufficientLast24Hours = 9
    renderDashboard()
    await openDetails()
    expect(screen.getByRole('alert')).toHaveTextContent('Son 24 saatte 9 yedekleme')
  })

  it('shows degraded policy-compliant copies without an insufficient-replication alert', () => {
    state.data.runs.replicationInsufficientLast24Hours = 0
    state.data.recentRuns = [makeRun({ status: 'Succeeded', replication: {
      requiredVerifiedReplicas: 2, verifiedReplicas: 2, destinationCount: 3,
      status: 'Degraded', policySatisfied: true, destinations: [],
    } })]
    renderDashboard()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    expect(screen.getByText("Kısmi çoğaltma · ilke karşılandı")).toBeInTheDocument()
  })

  it('keeps legacy responses without a replication count readable', () => {
    state.data.recentRuns = [makeRun({ status: 'Succeeded' })]
    renderDashboard()
    expect(screen.getAllByText('Başarılı').length).toBeGreaterThan(0)
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })
})

describe('dashboard failure notices', () => {
  it('reports delete failures and replication shortfalls as alerts with a way to investigate', () => {
    state.data.artifacts.deleteFailed = 2
    state.data.runs.replicationInsufficientLast24Hours = 1
    renderDashboard()
    expect(screen.getAllByRole('alert')).toHaveLength(2)
    expect(screen.getByRole('link', { name: 'Silme hatalarını inceleyin.' })).toHaveAttribute('href', '/tenants/tenant-a/artifacts?state=DeleteFailed')
  })

  it('keeps the failed-backups link to the filtered run history', () => {
    state.data.runs.failedLast24Hours = 1
    renderDashboard()
    expect(screen.getByRole('link', { name: 'Başarısız yedeklemeler' })).toHaveAttribute('href', '/tenants/tenant-a/backup-runs?status=Failed')
  })
})

describe('dashboard sentences and labels', () => {
  it('writes each alert as one whole sentence so the count sits where the language wants it', async () => {
    changeLanguage('en')
    state.data.artifacts.deleteFailed = 2
    state.data.runs.replicationInsufficientLast24Hours = 3
    renderDashboard()
    const alerts = screen.getAllByRole('alert').map(alert => alert.textContent)
    expect(alerts).toEqual(expect.arrayContaining([
      expect.stringContaining('2 backup file(s) could not be deleted.'),
      expect.stringContaining('3 backup(s) in the last 24 hours produced a usable backup but did not reach the required number of verified copies.'),
    ]))
  })

  it('names the counters in plain words instead of raw state names', async () => {
    renderDashboard()
    await openDetails()
    for (const label of ['Planlı yedekleme işleri', 'Yeniden denenecek', 'İptal isteniyor', 'Etkin yedekleme denemeleri', 'Silinmeyi bekleyen yedek dosyaları']) expect(screen.getByText(label)).toBeInTheDocument()
    for (const jargon of ['Scheduled Jobs', 'RetryPending', 'CancellationRequested', 'Aktif Runs', 'Etkin Jobs']) expect(screen.queryByText(jargon)).not.toBeInTheDocument()
  })
})

describe('dashboard recent runs', () => {
  it('keeps the database and replication inside the run\'s technical details and explains a waiting run in a sentence', () => {
    setViewport(true)
    state.data.recentRuns = [makeRun({ status: 'RetryPending', replication: { requiredVerifiedReplicas: 2, verifiedReplicas: 1, destinationCount: 2, status: 'InsufficientReplicas', policySatisfied: false, destinations: [] } })]
    renderDashboard()
    const row = screen.getAllByRole('row')[1]
    expect(within(row).getByText('Yeniden deneme bekleniyor.')).toBeVisible()
    expect(within(row).queryByText('RetryPending')).not.toBeInTheDocument()
    const details = within(row).getByText('Teknik ayrıntılar').closest('details')!
    expect(within(details).getByText('Doğrulanmış kopya sayısı yetersiz')).not.toBeVisible()
    expect(within(details).getByText(makeRun().configuration.database)).not.toBeVisible()
  })

  it('lists recent runs as cards on a narrow screen and as a table on a large one', () => {
    state.data.recentRuns = [makeRun({ status: 'Succeeded' })]
    setViewport(false)
    const narrow = renderDashboard()
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    expect(screen.getByRole('listitem')).toHaveTextContent('Günlük SQL yedeği')
    narrow.unmount()
    setViewport(true)
    renderDashboard()
    expect(screen.getByRole('table')).toBeInTheDocument()
  })

  it('does not list runs for a tenant that has no jobs yet', () => {
    state.observability = emptyObservability()
    renderDashboard()
    expect(screen.queryByText('Son yedeklemeler')).not.toBeInTheDocument()
  })
})

describe('observability dashboard', () => {
  it('hides the size-anomaly list while the sizeAnomaly feature is off', async () => {
    setFeatureFlags({})
    state.observability = { ...emptyObservability(), totalJobs: 1, averageScore: 70, state: 'Degraded', degraded: 1, jobs: [jobHealth('job-risk', 'Riskli veritabanı', 'Degraded', 70)] }
    renderDashboard()
    await openDetails()
    expect(screen.queryByText('Son 7 günün boyut anomalileri')).not.toBeInTheDocument()
  })

  it('links capacity risks and successful anomalous backups to their investigation screens', async () => {
    state.observability = {
      ...emptyObservability(), totalJobs: 1, averageScore: 70, state: 'Degraded', degraded: 1,
      jobs: [jobHealth('job-risk', 'Riskli veritabanı', 'Degraded', 70, { storageCapacity: { score: 0, maxScore: 10, status: 'Critical', reason: 'capacity_critical' } })],
      capacityRisks: [{ connectionId: 'disk-risk', connectionName: 'Dolu disk', status: 'Critical', totalBytes: 1_000_000_000, usedBytes: 920_000_000, freeBytes: 80_000_000, reportedAt: '2026-10-21T12:00:00Z' }],
      recentSizeAnomalies: [{ runId: 'run-anomaly', jobId: 'job-risk', jobName: 'Küçülen yedek', completedAt: '2026-10-21T12:00:00Z', sizeBytes: 600_000_000, baselineMeanBytes: 1_000_000_000, deviationRatio: -0.4 }],
      recentSizeAnomaliesTotal: 1,
    }
    state.data.recentRuns = [makeRun({ status: 'Succeeded' })]
    renderDashboard()
    await openDetails()
    expect(screen.getAllByRole('link', { name: /Riskli veritabanı/ }).map(link => link.getAttribute('href'))).toContain('/tenants/tenant-a/backup-jobs/job-risk/edit')
    expect(screen.getByRole('link', { name: 'Dolu disk' })).toHaveAttribute('href', '/tenants/tenant-a/storage/connections/disk-risk')
    expect(screen.getByRole('link', { name: 'Küçülen yedek' })).toHaveAttribute('href', '/tenants/tenant-a/backup-runs/run-anomaly')
    expect(screen.getByText('Kritik kapasite')).toBeInTheDocument()
    expect(screen.getByText(/Yedek: 600 MB · Geçmiş ortalama: 1 GB/)).toBeInTheDocument()
    expect(screen.getAllByText('Başarılı').length).toBeGreaterThan(0)
  })
})
