import { fireEvent, screen, within } from '@testing-library/react'
import { render } from '@/test/render'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { setViewport } from '@/test/viewport'
import { makeRun } from '@/features/backups/test-data'
import { dateFilterInstant } from '@/features/backups/presentation'
import type { BackupRunFilters, BackupRunPage } from '@/features/backups/types'
import { BackupsPage } from './backups-page'

const state = vi.hoisted(() => ({
  filters: [] as BackupRunFilters[],
  page: undefined as BackupRunPage | undefined,
}))
vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => ({ tenantId: 'tenant-a' }) }))
vi.mock('@/features/backups/hooks', () => ({ useBackups: (filters: BackupRunFilters) => { state.filters.push(filters); return { data: state.page, isPending: false, isFetching: false, error: null } } }))
vi.mock('@/features/backup-jobs/hooks', () => ({ useBackupJobs: () => ({ data: [{ id: 'job-id', name: 'Günlük SQL yedeği' }], isPending: false, error: null }) }))
vi.mock('@/features/agents/hooks', () => ({ useAgents: () => ({ data: [{ agentId: 'agent-id', hostName: 'SQL ajanı' }], isPending: false, error: null }) }))

beforeEach(() => {
  state.filters = []
  state.page = { items: [makeRun()], totalCount: 60, page: 1, pageSize: 25 }
})

describe('run history filters and pagination', () => {
  it('shows anomaly and signed comparison beside success while normal runs have no warning badge', () => {
    state.page = { items: [makeRun({ status: 'Succeeded', isSizeAnomaly: true, sizeAnomalyStatus: 'SizeAnomalyWarning', sizeAnomalyBaselineBytes: 5_000_000_000, sizeAnomalyDeviationRatio: 2 }),
      makeRun({ id: 'normal-run', status: 'Succeeded', sizeAnomalyStatus: 'Normal', sizeAnomalyBaselineBytes: 5_000_000_000, sizeAnomalyDeviationRatio: 0.05 })], totalCount: 2, page: 1, pageSize: 25 }
    render(<MemoryRouter><BackupsPage /></MemoryRouter>)
    const [warningRow, normalRow] = screen.getAllByRole('row').slice(1)
    expect(within(warningRow).getByText('Boyut Anomalisi')).toBeInTheDocument()
    expect(within(warningRow).getByText('Başarılı')).toBeInTheDocument()
    expect(within(normalRow).getByText('Başarılı')).toBeInTheDocument()
    expect(within(normalRow).queryByText('Boyut Anomalisi')).not.toBeInTheDocument()
    expect(within(warningRow).getByText(/Geçmiş ortalama: 5 GB/)).toHaveTextContent('+%200')
    expect(within(normalRow).getByText(/Geçmiş ortalama: 5 GB/)).toHaveTextContent('+%5')
  })

  it('submits all filters to query hook, resets page on change, and clears filters', () => {
    render(<MemoryRouter><BackupsPage /></MemoryRouter>)
    fireEvent.click(screen.getByRole('button', { name: 'Sonraki' }))
    expect(state.filters.at(-1)?.page).toBe(2)
    fireEvent.change(screen.getByRole('combobox', { name: 'Durum' }), { target: { value: 'Failed' } })
    fireEvent.change(screen.getByRole('combobox', { name: 'İş' }), { target: { value: 'job-id' } })
    fireEvent.change(screen.getByRole('combobox', { name: 'Yedekleme bilgisayarı' }), { target: { value: 'agent-id' } })
    fireEvent.change(screen.getByRole('combobox', { name: 'Tetikleyici' }), { target: { value: 'Retry' } })
    fireEvent.change(screen.getByLabelText('Oluşturulma başlangıcı'), { target: { value: '2026-10-01' } })
    fireEvent.change(screen.getByLabelText('Oluşturulma sonu'), { target: { value: '2026-10-06' } })
    expect(state.filters.at(-1)).toEqual({ page: 1, pageSize: 25, status: 'Failed', jobId: 'job-id', agentId: 'agent-id', trigger: 'Retry', from: dateFilterInstant('2026-10-01'), to: dateFilterInstant('2026-10-06', true) })
    fireEvent.click(screen.getByRole('button', { name: 'Filtreleri temizle' }))
    expect(state.filters.at(-1)).toEqual({ page: 1, pageSize: 25 })
    expect(screen.getByLabelText('Oluşturulma başlangıcı')).toHaveValue('')
  })

  it('shows job, result, start time and duration, with the job linking to the run and no raw ids', () => {
    render(<MemoryRouter><BackupsPage /></MemoryRouter>)
    for (const name of ['İş', 'Durum', 'Başlangıç', 'Süre']) {
      expect(screen.getByRole('columnheader', { name })).toBeInTheDocument()
    }
    expect(screen.getAllByRole('columnheader')).toHaveLength(4)
    const row = within(screen.getAllByRole('row')[1])
    expect(row.getByRole('link', { name: 'Günlük SQL yedeği' })).toHaveAttribute('href', '/tenants/tenant-a/backup-runs/run-id')
    expect(row.getByText('Çalışıyor')).toBeInTheDocument()
    expect(row.getByText(/önce/)).toBeInTheDocument()
    expect(screen.queryByText('job-id')).not.toBeInTheDocument()
    expect(screen.queryByText('agent-id')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Önceki' })).toBeDisabled()
  })

  it('keeps trigger, planned time, attempt counts and replication out of the list', () => {
    state.page = { items: [makeRun({ status: 'Succeeded', scheduledFor: '2026-10-06T09:59:00Z', replication: { requiredVerifiedReplicas: 2, verifiedReplicas: 1, destinationCount: 3, status: 'InsufficientReplicas', policySatisfied: false, destinations: [] } })], totalCount: 1, page: 1, pageSize: 25 }
    render(<MemoryRouter><BackupsPage /></MemoryRouter>)
    expect(screen.queryByRole('columnheader', { name: 'Tetikleyici' })).not.toBeInTheDocument()
    expect(screen.queryByRole('cell', { name: /Elle başlatıldı/ })).not.toBeInTheDocument()
    expect(screen.queryByText(/Planlanan/)).not.toBeInTheDocument()
    expect(screen.queryByText(/Deneme 1 \/ 3/)).not.toBeInTheDocument()
    expect(screen.queryByText('Doğrulanmış kopya sayısı yetersiz')).not.toBeInTheDocument()
    expect(screen.queryByText('Uygulama · SQL ajanı')).not.toBeInTheDocument()
  })

  it('on a phone shows each run as a card with the job, result, start time and duration', () => {
    setViewport(false)
    render(<MemoryRouter><BackupsPage /></MemoryRouter>)
    const card = within(screen.getByRole('listitem'))
    expect(card.getByRole('link', { name: 'Günlük SQL yedeği' })).toBeInTheDocument()
    expect(card.getByText('Çalışıyor')).toBeInTheDocument()
    expect(card.getAllByRole('term').map((term) => term.textContent)).toEqual(['Başlangıç', 'Süre'])
  })

  it('with no runs and no filters, points to the backup jobs; with filters, offers to clear them', () => {
    state.page = { items: [], totalCount: 0, page: 1, pageSize: 25 }
    render(<MemoryRouter><BackupsPage /></MemoryRouter>)
    expect(screen.getByRole('link', { name: /yedekleme işlerine/i })).toHaveAttribute('href', '/tenants/tenant-a/backup-jobs')
    expect(screen.queryByRole('button', { name: 'Filtreleri temizle' })).not.toBeInTheDocument()
    fireEvent.change(screen.getByRole('combobox', { name: 'Durum' }), { target: { value: 'Failed' } })
    expect(screen.getAllByRole('button', { name: 'Filtreleri temizle' }).length).toBeGreaterThan(0)
    expect(screen.queryByRole('link', { name: /yedekleme işlerine/i })).not.toBeInTheDocument()
  })
})
