import { fireEvent, screen, within } from '@testing-library/react'
import { render } from '@/test/render'
import { searchFor } from '@/test/search'
import { MemoryRouter, Route, Routes, useParams } from 'react-router-dom'
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
vi.mock('@/features/backup-jobs/hooks', () => ({ useBackupJobs: () => ({ data: [{ id: 'job-id', name: 'Günlük SQL yedeği' }, { id: 'old-job', name: 'Eski iş', archivedAt: '2026-10-01T00:00:00Z' }], isPending: false, error: null }) }))
vi.mock('./backup-detail-page', () => ({ BackupDetailPage: () => { const { id } = useParams(); return <p>detail {id}</p> } }))
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
    expect(within(warningRow).getByText('Boyut anomalisi')).toBeInTheDocument()
    expect(within(warningRow).getByText('Başarılı')).toBeInTheDocument()
    expect(within(normalRow).getByText('Başarılı')).toBeInTheDocument()
    expect(within(normalRow).queryByText('Boyut anomalisi')).not.toBeInTheDocument()
    expect(within(warningRow).getByText(/Geçmiş ortalama: 5 GB/)).toHaveTextContent('+%200')
    expect(within(normalRow).getByText(/Geçmiş ortalama: 5 GB/)).toHaveTextContent('+%5')
  })

  it.each([
    ['DeletePending', 'Silme bekliyor'],
    ['DeleteFailed', 'Silme başarısız'],
    ['Deleted', 'Silindi'],
    ['Available', null],
  ])('shows backup file details and deletion state %s on a successful run', (stateName, note) => {
    const run = makeRun({ status: 'Succeeded' })
    run.attempts[0].artifacts = [{
      id: 'artifact-id', sizeBytes: 2_000_000_000, sourceChecksum: '', artifactChecksum: '', checksumAlgorithm: '',
      createdAt: '2026-10-06T10:00:00Z', replicas: [], state: stateName, acceptedAt: null,
      retentionExpiresAt: '2026-12-01T12:00:00Z', retentionPinnedAt: null, deleteAfter: null, deleteReason: null,
      runId: run.id, jobId: run.jobId, attemptId: run.attempts[0].id,
      replication: { requiredVerifiedReplicas: 1, verifiedReplicas: 2, destinationCount: 2, status: 'Healthy', policySatisfied: true, destinations: [] },
    }]
    state.page = { items: [run], totalCount: 1, page: 1, pageSize: 25 }
    render(<MemoryRouter><BackupsPage /></MemoryRouter>)
    const row = within(screen.getAllByRole('row')[1])
    expect(row.getByText(/2 GB · 2 kopya/)).toBeInTheDocument()
    expect(row.getByText(/Saklama sonu:/)).toBeInTheDocument()
    if (note) expect(row.getByText(note)).toBeInTheDocument()
    else expect(row.queryByText('Silme bekliyor')).not.toBeInTheDocument()
  })

  it('submits all filters to query hook, resets page on change, and clears filters', () => {
    render(<MemoryRouter><BackupsPage /></MemoryRouter>)
    expect(screen.getByRole('combobox', { name: 'Durum' })).toBeVisible()
    expect(screen.queryByLabelText('Tetikleyici')).not.toBeInTheDocument()
    fireEvent.click(screen.getByText('Diğer filtreler'))
    fireEvent.click(screen.getByRole('button', { name: 'Sonraki' }))
    expect(state.filters.at(-1)?.page).toBe(2)
    fireEvent.change(screen.getByRole('combobox', { name: 'Durum' }), { target: { value: 'Failed' } })
    fireEvent.change(screen.getByRole('combobox', { name: 'İş' }), { target: { value: 'job-id' } })
    fireEvent.change(screen.getByRole('combobox', { name: 'Yedekleme bilgisayarı' }), { target: { value: 'agent-id' } })
    fireEvent.change(screen.getByLabelText('Oluşturulma başlangıcı'), { target: { value: '2026-10-01' } })
    fireEvent.change(screen.getByLabelText('Oluşturulma sonu'), { target: { value: '2026-10-06' } })
    expect(state.filters.at(-1)).toEqual({ page: 1, pageSize: 25, status: 'Failed', jobId: 'job-id', agentId: 'agent-id', from: dateFilterInstant('2026-10-01'), to: dateFilterInstant('2026-10-06', true) })
    fireEvent.click(screen.getByRole('button', { name: 'Filtreleri temizle' }))
    expect(state.filters.at(-1)).toEqual({ page: 1, pageSize: 25 })
    expect(screen.getByLabelText('Oluşturulma başlangıcı')).toHaveValue('')
  })

  it('shows job, result, start time and duration, with the job linking to the run and no raw ids', () => {
    render(<MemoryRouter><BackupsPage /></MemoryRouter>)
    for (const name of ['İş', 'Durum', 'Yedek dosyası', 'Oluşturulma', 'Başlangıç', 'Süre']) {
      expect(screen.getByRole('columnheader', { name })).toBeInTheDocument()
    }
    expect(screen.getAllByRole('columnheader')).toHaveLength(6)
    const row = within(screen.getAllByRole('row')[1])
    expect(row.getByRole('link', { name: 'Günlük SQL yedeği' })).toHaveAttribute('href', '/tenants/tenant-a/backup-runs/run-id')
    expect(row.getByText('Çalışıyor')).toBeInTheDocument()
    expect(row.getAllByText(/önce/)).toHaveLength(2)
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
    expect(card.getAllByRole('term').map((term) => term.textContent)).toEqual(['Yedek dosyası', 'Oluşturulma', 'Başlangıç', 'Süre'])
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
    expect(screen.queryByText('Boyut anomalisi')).not.toBeInTheDocument()
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

  it('a job link filters the history and date and page filters survive a reload', () => {
    mountAt('/?jobId=job-id&agentId=agent-id&trigger=Retry&from=2026-10-01&to=2026-10-06&page=2')
    expect(state.filters.at(-1)).toEqual({ page: 2, pageSize: 25, jobId: 'job-id', agentId: 'agent-id', from: dateFilterInstant('2026-10-01'), to: dateFilterInstant('2026-10-06', true) })
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
    fireEvent.click(screen.getByText('Diğer filtreler'))
    expect(screen.getByTestId('location')).toHaveTextContent('/?status=Failed')
    fireEvent.change(screen.getByLabelText('Oluşturulma başlangıcı'), { target: { value: '2026-10-01' } })
    expect(screen.getByTestId('location')).toHaveTextContent('/?status=Failed&from=2026-10-01')
    fireEvent.click(screen.getByRole('button', { name: 'Filtreleri temizle' }))
    expect(screen.getByTestId('location')).toHaveTextContent(/^\/$/)
  })
})

describe('run history search, sorting and paging', () => {
  const mountAt = (url: string) => render(<MemoryRouter initialEntries={[url]}><BackupsPage /><LocationProbe /></MemoryRouter>)
  const header = (name: string) => screen.getByRole('columnheader', { name })
  const location = () => screen.getByTestId('location')
  const sortHeaders = ['İş', 'Durum', 'Yedek dosyası', 'Başlangıç', 'Süre']

  it('asks for the newest runs first without sending a sort, and shows that order on the Oluşturulma column', () => {
    mountAt('/')
    expect(state.filters.at(-1)).toEqual({ page: 1, pageSize: 25 })
    expect(header('Oluşturulma')).toHaveAttribute('aria-sort', 'descending')
    for (const name of sortHeaders) expect(header(name)).toHaveAttribute('aria-sort', 'none')
  })

  it('on a phone the Sırala select shows the real default order', () => {
    setViewport(false)
    mountAt('/')
    expect(screen.getByRole('combobox', { name: 'Sırala' })).toHaveValue('createdAt:desc')
    expect(screen.getByRole('option', { name: 'Oluşturulma, azalan' })).toBeInTheDocument()
  })

  it.each([
    ['İş', 'jobName', 'asc'],
    ['Durum', 'status', 'asc'],
    ['Yedek dosyası', 'size', 'asc'],
    ['Başlangıç', 'startedAt', 'desc'],
    ['Süre', 'duration', 'asc'],
  ])('the %s header sorts by %s, %s first, and the next click reverses it', (name, field, first) => {
    mountAt('/?page=2')
    fireEvent.click(within(header(name)).getByRole('button'))
    expect(state.filters.at(-1)).toEqual({ page: 1, pageSize: 25, sort: field, dir: first })
    expect(header(name)).toHaveAttribute('aria-sort', first === 'asc' ? 'ascending' : 'descending')
    expect(location()).toHaveTextContent(`/?sort=${field}&dir=${first}`)
    fireEvent.click(within(header(name)).getByRole('button'))
    expect(state.filters.at(-1)).toMatchObject({ sort: field, dir: first === 'asc' ? 'desc' : 'asc' })
    expect(header(name)).toHaveAttribute('aria-sort', first === 'asc' ? 'descending' : 'ascending')
  })

  it('the Başlangıç header sorts by the start time it shows, not by the creation time', () => {
    mountAt('/')
    fireEvent.click(within(header('Başlangıç')).getByRole('button'))
    expect(state.filters.at(-1)?.sort).toBe('startedAt')
    expect(state.filters.at(-1)?.sort).not.toBe('createdAt')
  })

  it('searches the runs, keeps the filters and goes back to the first page', () => {
    mountAt('/?status=Failed&page=2')
    searchFor(screen.getByRole('searchbox', { name: 'Ara' }), 'günlük')
    expect(state.filters.at(-1)).toEqual({ page: 1, pageSize: 25, status: 'Failed', search: 'günlük' })
    expect(location()).toHaveTextContent(/^\/\?status=Failed&search=g%C3%BCnl%C3%BCk$/)
  })

  it('sends the search trimmed and not at all when it is blank', () => {
    mountAt('/')
    const box = screen.getByRole('searchbox', { name: 'Ara' })
    searchFor(box, '  sql  ')
    expect(state.filters.at(-1)).toEqual({ page: 1, pageSize: 25, search: 'sql' })
    searchFor(box, '   ')
    expect(state.filters.at(-1)).toEqual({ page: 1, pageSize: 25 })
  })

  it('restores the search, the sort and the page from the URL', () => {
    mountAt('/?search=sql&status=Succeeded&sort=duration&dir=asc&page=3')
    expect(state.filters.at(-1)).toEqual({ page: 3, pageSize: 25, search: 'sql', status: 'Succeeded', sort: 'duration', dir: 'asc' })
    expect(screen.getByRole('searchbox', { name: 'Ara' })).toHaveValue('sql')
    expect(header('Süre')).toHaveAttribute('aria-sort', 'ascending')
  })

  it('a direction alone in the URL sorts the default field and an unknown sort is ignored', () => {
    mountAt('/?sort=password&dir=sideways')
    expect(state.filters.at(-1)).toEqual({ page: 1, pageSize: 25 })
    expect(header('Oluşturulma')).toHaveAttribute('aria-sort', 'descending')
    for (const name of sortHeaders) expect(header(name)).toHaveAttribute('aria-sort', 'none')
  })

  it('a link with the creation sort keeps working', () => {
    mountAt('/?sort=createdAt&dir=asc')
    expect(state.filters.at(-1)).toEqual({ page: 1, pageSize: 25, sort: 'createdAt', dir: 'asc' })
  })

  it('pages with the search and sort in place, and a new sort goes back to page 1', () => {
    mountAt('/?search=sql&sort=size&dir=desc')
    fireEvent.click(screen.getByRole('button', { name: 'Sonraki' }))
    expect(state.filters.at(-1)).toEqual({ page: 2, pageSize: 25, search: 'sql', sort: 'size', dir: 'desc' })
    expect(location()).toHaveTextContent('search=sql&sort=size&dir=desc&page=2')
    fireEvent.click(within(header('Süre')).getByRole('button'))
    expect(state.filters.at(-1)).toEqual({ page: 1, pageSize: 25, search: 'sql', sort: 'duration', dir: 'asc' })
  })

  it('says nothing matched the search and clears it, keeping the sort', () => {
    state.page = { items: [], totalCount: 0, page: 1, pageSize: 25 }
    mountAt('/?search=yok&sort=duration&dir=asc')
    expect(screen.getByText('Bu filtrelere uygun yedekleme denemesi bulunamadı.')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Filtreleri temizle' }))
    expect(state.filters.at(-1)).toEqual({ page: 1, pageSize: 25, sort: 'duration', dir: 'asc' })
    expect(location()).toHaveTextContent(/^\/\?sort=duration&dir=asc$/)
  })
})

describe('history side panel', () => {
  const mountRun = (url: string) => render(<MemoryRouter initialEntries={[url]}><Routes><Route path="/backup-runs" element={<BackupsPage />} /><Route path="/backup-runs/:id" element={<BackupsPage />} /></Routes></MemoryRouter>)

  it('opens the selected attempt beside the list, keeps the filters in the row links and marks the open row', () => {
    mountRun('/backup-runs/run-id?status=Succeeded')
    expect(screen.getByRole('region', { name: 'Yedekleme denemesi' })).toHaveTextContent('detail run-id')
    expect(screen.getByRole('link', { name: 'Günlük SQL yedeği' })).toHaveAttribute('aria-current', 'page')
    expect(screen.getByRole('link', { name: 'Günlük SQL yedeği' })).toHaveAttribute('href', '/tenants/tenant-a/backup-runs/run-id?status=Succeeded')
    expect(screen.queryByRole('columnheader', { name: 'Başlangıç' })).not.toBeInTheDocument()
  })

  it('shows no panel without a selected attempt', () => {
    mountRun('/backup-runs')
    expect(screen.queryByRole('region', { name: 'Yedekleme denemesi' })).not.toBeInTheDocument()
  })

  it('lists an archived job in the job filter with a removed suffix', () => {
    mountRun('/backup-runs')
    expect(within(screen.getByRole('combobox', { name: 'İş' })).getByRole('option', { name: 'Eski iş (kaldırıldı)' })).toBeInTheDocument()
  })
})
