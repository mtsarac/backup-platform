import { fireEvent, screen, within } from '@testing-library/react'
import { render } from '@/test/render'
import { MemoryRouter } from 'react-router-dom'
import { LocationProbe } from '@/features/platform/location-probe'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { setFeatureFlags } from '@/features/feature-flags/feature-flags'
import { setViewport } from '@/test/viewport'
import { makeRun } from '@/features/backups/test-data'
import { dateFilterInstant } from '@/features/backups/presentation'
import type { BackupRunFilters, BackupRunPage } from '@/features/backups/types'
import { BackupsPage } from './backups-page'

const state = vi.hoisted(() => ({
  filters: [] as BackupRunFilters[],
  page: undefined as BackupRunPage | undefined,
}))
vi.mock('@tanstack/react-query', async importOriginal => ({ ...await importOriginal<typeof import('@tanstack/react-query')>(), useQueryClient: () => ({ refetchQueries: vi.fn() }) }))
vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => ({ tenantId: 'tenant-a' }) }))
vi.mock('@/features/backups/hooks', () => ({ useBackups: (filters: BackupRunFilters) => { state.filters.push(filters); return { data: state.page, isPending: false, isFetching: false, error: null } } }))
vi.mock('@/features/backup-jobs/hooks', () => ({ useBackupJobs: () => ({ data: [{ id: 'job-id', name: 'Günlük SQL yedeği' }], isPending: false, error: null }) }))
vi.mock('@/features/agents/hooks', () => ({ useAgents: () => ({ data: [{ agentId: 'agent-id', hostName: 'SQL ajanı' }], isPending: false, error: null }) }))

beforeEach(() => {
  setFeatureFlags({ sizeAnomaly: true })
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

  it('on a phone clears an empty filtered history from its filters and restores the first-run action', () => {
    setViewport(false)
    state.page = { items: [], totalCount: 0, page: 1, pageSize: 25 }
    render(<MemoryRouter initialEntries={['/backup-runs?status=Failed']}><BackupsPage /><LocationProbe /></MemoryRouter>)
    expect(screen.getByRole('combobox', { name: 'Durum' })).toHaveValue('Failed')
    expect(screen.queryByRole('link', { name: /yedekleme işlerine/i })).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Filtreleri temizle' }))
    expect(state.filters.at(-1)).toEqual({ page: 1, pageSize: 25 })
    expect(screen.getByRole('combobox', { name: 'Durum' })).toHaveValue('')
    expect(screen.getByTestId('location')).toHaveTextContent(/^\/backup-runs$/)
    expect(screen.getByRole('link', { name: /yedekleme işlerine/i })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Filtreleri temizle' })).not.toBeInTheDocument()
  })
})

describe('size anomaly feature off', () => {
  it('shows no anomaly badge or comparison', () => {
    setFeatureFlags({})
    state.page = { items: [makeRun({ status: 'Succeeded', isSizeAnomaly: true, sizeAnomalyStatus: 'SizeAnomalyWarning', sizeAnomalyBaselineBytes: 5_000_000_000, sizeAnomalyDeviationRatio: 2 })], totalCount: 1, page: 1, pageSize: 25 }
    render(<MemoryRouter><BackupsPage /></MemoryRouter>)
    expect(screen.queryByText('Boyut Anomalisi')).not.toBeInTheDocument()
    expect(screen.queryByText(/Geçmiş ortalama/)).not.toBeInTheDocument()
  })
})

describe('run history URL state', () => {
  const mountAt = (url: string) => render(<MemoryRouter initialEntries={[url]}><BackupsPage /><LocationProbe /></MemoryRouter>)

  it.each([['Failed'], ['Succeeded']])('a dashboard link with status=%s filters the list and shows the filter', (status) => {
    mountAt(`/?status=${status}`)
    expect(state.filters.at(-1)).toMatchObject({ status, page: 1 })
    expect(screen.getByRole('combobox', { name: 'Durum' })).toHaveValue(status)
    expect(screen.getAllByRole('button', { name: 'Filtreleri temizle' })).toHaveLength(1)
  })

  it('a job link filters the history to that job, and the date, trigger and page filters survive a reload', () => {
    mountAt('/?jobId=job-id&agentId=agent-id&trigger=Retry&from=2026-10-01&to=2026-10-06&page=2')
    expect(state.filters.at(-1)).toEqual({ page: 2, pageSize: 25, jobId: 'job-id', agentId: 'agent-id', trigger: 'Retry', from: dateFilterInstant('2026-10-01'), to: dateFilterInstant('2026-10-06', true) })
    expect(screen.getByRole('combobox', { name: 'İş' })).toHaveValue('job-id')
    expect(screen.getByLabelText('Oluşturulma başlangıcı')).toHaveValue('2026-10-01')
  })

  it('ignores a status, trigger or date that is not a real value instead of sending it to the server', () => {
    mountAt('/?status=Nonsense&trigger=Nope&from=yesterday&page=-3')
    expect(state.filters.at(-1)).toEqual({ page: 1, pageSize: 25 })
    expect(screen.queryByRole('button', { name: 'Filtreleri temizle' })).not.toBeInTheDocument()
  })

  it.each([
    '2026-02-31', '2026-02-29', '1900-02-29', '2026-04-31',
    '2026-13-01', '2026-00-01', '2026-01-00', '2026-01-32',
    '2026-2-01', '2026-02-1', '2026-02-01T00:00:00Z', ' 2026-02-01', 'not-a-date',
  ])('ignores invalid URL day %s in both date filters', (day) => {
    mountAt(`/?from=${encodeURIComponent(day)}&to=${encodeURIComponent(day)}`)
    expect(state.filters.at(-1)).toEqual({ page: 1, pageSize: 25 })
    expect(screen.getByLabelText('Oluşturulma başlangıcı')).toHaveValue('')
    expect(screen.getByLabelText('Oluşturulma sonu')).toHaveValue('')
    expect(screen.queryByRole('button', { name: 'Filtreleri temizle' })).not.toBeInTheDocument()
  })

  it.each(['2024-02-29', '2000-02-29'])('uses valid leap day %s from the URL without changing the calendar day', (day) => {
    mountAt(`/?from=${day}&to=${day}`)
    expect(state.filters.at(-1)).toEqual({ page: 1, pageSize: 25, from: dateFilterInstant(day), to: dateFilterInstant(day, true) })
    expect(screen.getByLabelText('Oluşturulma başlangıcı')).toHaveValue(day)
    expect(screen.getByLabelText('Oluşturulma sonu')).toHaveValue(day)
  })

  it('writes every filter and page change to the URL; changing a filter drops the page and clearing empties the URL', () => {
    mountAt('/')
    fireEvent.click(screen.getByRole('button', { name: 'Sonraki' }))
    expect(screen.getByTestId('location')).toHaveTextContent('/?page=2')
    fireEvent.change(screen.getByRole('combobox', { name: 'Durum' }), { target: { value: 'Failed' } })
    expect(screen.getByTestId('location')).toHaveTextContent('/?status=Failed')
    fireEvent.change(screen.getByLabelText('Oluşturulma başlangıcı'), { target: { value: '2026-10-01' } })
    expect(screen.getByTestId('location')).toHaveTextContent('/?status=Failed&from=2026-10-01')
    fireEvent.click(screen.getByRole('button', { name: 'Filtreleri temizle' }))
    expect(screen.getByTestId('location')).toHaveTextContent(/^\/$/)
  })
})
