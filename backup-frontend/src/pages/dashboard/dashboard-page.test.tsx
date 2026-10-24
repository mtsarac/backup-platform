import { screen, within } from '@testing-library/react'
import { render } from '@/test/render'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { setViewport } from '@/test/viewport'
import { makeRun } from '@/features/backups/test-data'
import type { BackupRun } from '@/features/backups/types'
import { DashboardPage } from './dashboard-page'

const state = vi.hoisted(() => ({
  data: {
    jobs: { total: 1, enabled: 1, scheduled: 0 },
    agents: { total: 1, online: 1, offline: 0, unknown: 0, disabled: 0, revoked: 0 },
    runs: { active: 0, running: 0, retryPending: 0, cancellationRequested: 0, storageFailedLast24Hours: 0, failedLast24Hours: 0, succeededLast24Hours: 1, replicationInsufficientLast24Hours: undefined as number | undefined },
    artifacts: { total: 1, sizeBytes: 100, deletePending: 0, deleteFailed: 0 },
    recentRuns: [] as BackupRun[],
    upcomingJobs: [],
  },
  observability: {
    totalJobs: 0, averageScore: null as number | null, healthy: 0, degraded: 0, critical: 0,
    jobs: [] as { jobId: string; jobName: string; score: number; state: string }[],
    capacityRisks: [] as { connectionId: string; connectionName: string; status: string; totalBytes: number; usedBytes: number; freeBytes: number; reportedAt: string }[],
    recentSizeAnomalies: [] as { runId: string; jobId: string; jobName: string; completedAt: string; sizeBytes: number; baselineMeanBytes: number; deviationRatio: number }[],
    recentSizeAnomaliesTotal: 0,
  },
}))
vi.mock('@tanstack/react-query', () => ({ useQuery: ({ queryKey }: { queryKey: string[] }) => ({ data: queryKey.includes('observability') ? state.observability : state.data, isPending: false, error: null }) }))
vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => ({ tenantId: 'tenant-a', tenant: { name: 'Tenant A' } }) }))

function renderDashboard() {
  return render(<MemoryRouter><DashboardPage /></MemoryRouter>)
}

beforeEach(() => {
  localStorage.clear()
  state.data.agents.offline = 0
  state.data.runs.failedLast24Hours = 0
  state.data.runs.succeededLast24Hours = 1
  state.data.runs.storageFailedLast24Hours = 0
  state.data.artifacts.deleteFailed = 0
  state.data.runs.replicationInsufficientLast24Hours = undefined
  state.data.recentRuns = []
  state.observability = { totalJobs: 0, averageScore: null, healthy: 0, degraded: 0, critical: 0, jobs: [], capacityRisks: [], recentSizeAnomalies: [], recentSizeAnomaliesTotal: 0 }
})

describe('dashboard replication monitoring', () => {
  it('alerts for insufficient replication while retaining the available backup and engine success', () => {
    state.data.runs.replicationInsufficientLast24Hours = 1
    state.data.recentRuns = [makeRun({ status: 'Succeeded', replication: {
      requiredVerifiedReplicas: 3, verifiedReplicas: 2, destinationCount: 3,
      status: 'InsufficientReplicas', policySatisfied: false, destinations: [],
    } })]
    renderDashboard()
    expect(screen.getByRole('alert')).toHaveTextContent(/1 (?:backup|yedekleme) son 24 saatte kullanılabilir yedek (?:üretti|oluşturdu), ancak gereken doğrulanmış (?:replika|kopya) sayısına ulaşamadı\./)
    expect(screen.getByRole('link', { name: "Çoğaltma sonuçlarını inceleyin." })).toHaveAttribute('href', '/tenants/tenant-a/backup-runs?status=Succeeded')
    expect(screen.getAllByText('Başarılı').length).toBeGreaterThan(0)
    expect(screen.getByText("Doğrulanmış kopya sayısı yetersiz")).toBeInTheDocument()
    expect(within(screen.getByText("Yedek oluşturuldu · son 24 saat").parentElement!.parentElement!).getByText('1')).toBeInTheDocument()
  })

  it('alerts from the tenant-wide count even when the insufficient run is outside recent history', () => {
    state.data.runs.replicationInsufficientLast24Hours = 9
    renderDashboard()
    expect(screen.getByRole('alert')).toHaveTextContent(/9 (?:backup|yedekleme) son 24 saatte/)
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

describe('dashboard simplified status', () => {
  it('reports categories rather than overlapping error totals', () => {
    state.data.runs.failedLast24Hours = 8
    state.data.runs.storageFailedLast24Hours = 6
    state.data.runs.replicationInsufficientLast24Hours = 3
    state.data.artifacts.deleteFailed = 2
    renderDashboard()
    expect(screen.getByRole('status')).toHaveTextContent('4 kategoride uyarı var')
    expect(screen.getByRole('status')).toHaveTextContent('bu sayı toplam hata sayısı değildir')
    expect(screen.queryByText('Sistem güvende')).not.toBeInTheDocument()
  })

  it('limits a success statement to the measured KPI even with an offline agent', () => {
    state.data.runs.succeededLast24Hours = 7
    state.data.agents.offline = 1
    renderDashboard()
    expect(screen.getByRole('status')).toHaveTextContent(/Son 24 saatte 7 başarılı (?:yedek|yedekleme)/)
    expect(screen.getByRole('status')).toHaveTextContent(/tüm (?:agent|ajan) bağlantılarının veya planlı (?:görevlerin|işlerin) sağlıklı olduğunu göstermez/)
  })

  it('reports no successful backup when the KPI is zero', () => {
    state.data.runs.succeededLast24Hours = 0
    renderDashboard()
    expect(screen.getByRole('status')).toHaveTextContent("Son 24 saatte yedekleme yapılmadı")
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
})

describe('observability dashboard', () => {
  it('links capacity risks and successful anomalous backups to their investigation screens', () => {
    state.observability = {
      totalJobs: 1, averageScore: 70, healthy: 0, degraded: 1, critical: 0,
      jobs: [{ jobId: 'job-risk', jobName: 'Riskli veritabanı', score: 70, state: 'Degraded' }],
      capacityRisks: [{ connectionId: 'disk-risk', connectionName: 'Dolu disk', status: 'Critical', totalBytes: 1_000_000_000, usedBytes: 920_000_000, freeBytes: 80_000_000, reportedAt: '2026-10-21T12:00:00Z' }],
      recentSizeAnomalies: [{ runId: 'run-anomaly', jobId: 'job-risk', jobName: 'Küçülen yedek', completedAt: '2026-10-21T12:00:00Z', sizeBytes: 600_000_000, baselineMeanBytes: 1_000_000_000, deviationRatio: -0.4 }],
      recentSizeAnomaliesTotal: 1,
    }
    state.data.recentRuns = [makeRun({ status: 'Succeeded' })]
    renderDashboard()
    expect(screen.getByRole('link', { name: 'Riskli veritabanı' })).toHaveAttribute('href', '/tenants/tenant-a/backup-jobs/job-risk/edit')
    expect(screen.getByRole('link', { name: 'Dolu disk' })).toHaveAttribute('href', '/tenants/tenant-a/storage/connections/disk-risk')
    expect(screen.getByRole('link', { name: 'Küçülen yedek' })).toHaveAttribute('href', '/tenants/tenant-a/backup-runs/run-anomaly')
    expect(screen.getByText('Kritik kapasite')).toBeInTheDocument()
    expect(screen.getByText(/Yedek: 600 MB · Geçmiş ortalama: 1 GB/)).toBeInTheDocument()
    expect(screen.getAllByText('Başarılı').length).toBeGreaterThan(0)
  })
})
