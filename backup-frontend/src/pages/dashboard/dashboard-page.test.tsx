import { render, screen, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
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
}))
vi.mock('@tanstack/react-query', () => ({ useQuery: () => ({ data: state.data, isPending: false, error: null }) }))
vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => ({ tenantId: 'tenant-a', tenant: { name: 'Tenant A' } }) }))

function renderDashboard() {
  return render(<MemoryRouter><DashboardPage /></MemoryRouter>)
}

beforeEach(() => {
  state.data.runs.replicationInsufficientLast24Hours = undefined
  state.data.recentRuns = []
})

describe('dashboard replication monitoring', () => {
  it('alerts for insufficient replication while retaining the available backup and engine success', () => {
    state.data.runs.replicationInsufficientLast24Hours = 1
    state.data.recentRuns = [makeRun({ status: 'Succeeded', replication: {
      requiredVerifiedReplicas: 3, verifiedReplicas: 2, destinationCount: 3,
      status: 'InsufficientReplicas', policySatisfied: false, destinations: [],
    } })]
    renderDashboard()
    expect(screen.getByRole('alert')).toHaveTextContent('1 backup son 24 saatte kullanılabilir yedek üretti, ancak gereken doğrulanmış replika sayısına ulaşamadı.')
    expect(screen.getByRole('link', { name: 'Replikasyon sonuçlarını inceleyin.' })).toHaveAttribute('href', '/tenants/tenant-a/backup-runs?status=Succeeded')
    expect(screen.getByText('Başarılı · Succeeded')).toBeInTheDocument()
    expect(screen.getByText('Yetersiz doğrulanmış replika')).toBeInTheDocument()
    expect(within(screen.getByText('Yedek üretildi · son 24 saat').parentElement!.parentElement!).getByText('1')).toBeInTheDocument()
  })

  it('alerts from the tenant-wide count even when the insufficient run is outside recent history', () => {
    state.data.runs.replicationInsufficientLast24Hours = 9
    renderDashboard()
    expect(screen.getByRole('alert')).toHaveTextContent('9 backup son 24 saatte')
  })

  it('shows degraded policy-compliant copies without an insufficient-replication alert', () => {
    state.data.runs.replicationInsufficientLast24Hours = 0
    state.data.recentRuns = [makeRun({ status: 'Succeeded', replication: {
      requiredVerifiedReplicas: 2, verifiedReplicas: 2, destinationCount: 3,
      status: 'Degraded', policySatisfied: true, destinations: [],
    } })]
    renderDashboard()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    expect(screen.getByText('Kısmi replikasyon · politika karşılandı')).toBeInTheDocument()
  })

  it('keeps legacy responses without a replication count readable', () => {
    state.data.recentRuns = [makeRun({ status: 'Succeeded' })]
    renderDashboard()
    expect(screen.getByText('Başarılı · Succeeded')).toBeInTheDocument()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })
})
