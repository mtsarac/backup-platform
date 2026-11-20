import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { render } from '@/test/render'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { getAgents } from '@/features/agents/api'
import { getBackupJobs } from '@/features/backup-jobs/api'
import { getBackups } from '@/features/backups/api'
import { dateFilterInstant } from '@/features/backups/presentation'
import { makeRun } from '@/features/backups/test-data'
import type { BackupRunFilters } from '@/features/backups/types'
import { MAX_PAGE } from '@/features/lists/url-params'
import { LocationProbe } from '@/features/platform/location-probe'
import { BackupsPage } from './backups-page'

vi.mock('@/features/backups/api')
vi.mock('@/features/backup-jobs/api')
vi.mock('@/features/agents/api')
vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => ({ tenantId: 'tenant-a' }) }))

const runs = (count: number, offset = 0) => Array.from({ length: count }, (_, index) => makeRun({ id: `run-${offset + index}`, status: 'Succeeded', jobName: `İş ${offset + index}` }))
/** 31 runs at 25 a page, like the QA tenant; `total` can be lowered to get a single page. */
let total = 31
function answer(filters: BackupRunFilters) {
  const page = filters.page ?? 1
  const first = (page - 1) * 25
  return Promise.resolve({ items: first >= total ? [] : runs(Math.min(25, total - first), first), totalCount: total, page, pageSize: 25 })
}
const pagesAsked = () => vi.mocked(getBackups).mock.calls.map(([, filters]) => filters?.page)
const location = () => screen.getByTestId('location')

function mount(url = '/') {
  render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}><MemoryRouter initialEntries={[url]}><BackupsPage /><LocationProbe /></MemoryRouter></QueryClientProvider>)
}

beforeEach(() => {
  total = 31
  vi.mocked(getBackups).mockImplementation((_tenant, filters) => answer(filters ?? {}))
  vi.mocked(getBackupJobs).mockResolvedValue([])
  vi.mocked(getAgents).mockResolvedValue([])
})

describe('run history: page past the last', () => {
  it('falls back to the last page and never says there is nothing yet while there are runs', async () => {
    mount('/?page=5')
    await waitFor(() => expect(location()).toHaveTextContent('/?page=2'))
    expect(await screen.findByText('31 kayıt · Sayfa 2 / 2')).toBeInTheDocument()
    await waitFor(() => expect(screen.getAllByRole('row')).toHaveLength(7))
    expect(pagesAsked()).toEqual([5, 2])
    expect(screen.queryByText(/Henüz yedekleme denemesi yok/)).not.toBeInTheDocument()
  })

  it('caps an absurd page before it reaches the API', async () => {
    mount('/?page=2147483648')
    await waitFor(() => expect(location()).toHaveTextContent('/?page=2'))
    expect(await screen.findByText('31 kayıt · Sayfa 2 / 2')).toBeInTheDocument()
    expect(pagesAsked()[0]).toBe(MAX_PAGE)
    expect(pagesAsked()).not.toContain(2147483648)
    expect(screen.queryByText(/Veriler alınamadı/)).not.toBeInTheDocument()
  })

  it('falls back to page 1 when the whole list fits on it', async () => {
    total = 10
    mount('/?page=3')
    expect(await screen.findByText('10 kayıt')).toBeInTheDocument()
    await waitFor(() => expect(location()).toHaveTextContent(/^\/$/))
  })
})

describe('run history: count and default order', () => {
  it('shows the record count when everything fits on one page, without page buttons', async () => {
    total = 6
    mount()
    const paging = within(await screen.findByRole('navigation', { name: 'Yedekleme sayfaları' }))
    expect(paging.getByText('6 kayıt')).toBeInTheDocument()
    expect(paging.queryByRole('button')).not.toBeInTheDocument()
  })

  it('marks the Oluşturulma column as the default order', async () => {
    mount()
    expect(await screen.findByRole('columnheader', { name: 'Oluşturulma' })).toHaveAttribute('aria-sort', 'descending')
  })
})

describe('run history: keyboard sorting', () => {
  it('keeps focus on the header button while the sorted rows load', async () => {
    const user = userEvent.setup()
    mount()
    const button = within(await screen.findByRole('columnheader', { name: 'İş' })).getByRole('button')
    let release: () => void = () => undefined
    vi.mocked(getBackups).mockImplementationOnce((_tenant, filters) => new Promise(resolve => { release = () => resolve(answer(filters ?? {})) }))
    button.focus()
    await user.keyboard('{Enter}')
    await waitFor(() => expect(location()).toHaveTextContent('sort=jobName&dir=asc'))
    expect(button).toBeInTheDocument()
    expect(document.activeElement).toBe(button)
    release()
    await waitFor(() => expect(getBackups).toHaveBeenCalledTimes(2))
    expect(await screen.findAllByRole('row')).toHaveLength(26)
    expect(document.activeElement).toBe(button)
    await user.keyboard('{Enter}')
    await waitFor(() => expect(location()).toHaveTextContent('sort=jobName&dir=desc'))
  })
})

describe('run history: date filter', () => {
  const field = async (name: string) => { fireEvent.click(await screen.findByText('Diğer filtreler')); return screen.getByLabelText(name) }

  it('writes a date to the URL only once it is complete, so typing the year digit by digit does not reload the list', async () => {
    mount()
    const from = await field('Oluşturulma başlangıcı')
    await screen.findAllByRole('row')
    const asked = vi.mocked(getBackups).mock.calls.length
    from.focus()
    for (const partial of ['0002-11-19', '0020-11-19', '0202-11-19']) {
      fireEvent.change(from, { target: { value: partial } })
      expect(location()).not.toHaveTextContent('from=')
      expect(from).toHaveValue(partial)
    }
    expect(vi.mocked(getBackups).mock.calls).toHaveLength(asked)
    fireEvent.change(from, { target: { value: '2026-11-19' } })
    expect(location()).toHaveTextContent('/?from=2026-11-19')
    expect(from).toHaveValue('2026-11-19')
    await waitFor(() => expect(vi.mocked(getBackups).mock.lastCall?.[1]).toMatchObject({ from: dateFilterInstant('2026-11-19') }))
    expect(document.activeElement).toBe(from)
  })

  it('takes a real day that is still incomplete when the field loses focus', async () => {
    mount()
    const from = await field('Oluşturulma başlangıcı')
    fireEvent.change(from, { target: { value: '0202-11-19' } })
    expect(location()).not.toHaveTextContent('from=')
    fireEvent.blur(from)
    expect(location()).toHaveTextContent('from=0202-11-19')
  })

  it('clears the filter on blur when the field is emptied', async () => {
    mount('/?from=2026-11-19')
    const from = await field('Oluşturulma başlangıcı')
    fireEvent.change(from, { target: { value: '' } })
    expect(location()).toHaveTextContent('from=2026-11-19')
    fireEvent.blur(from)
    expect(location()).toHaveTextContent(/^\/$/)
  })

  it('says a start after the end next to the date fields and sends nothing for it', async () => {
    mount('/?from=2026-12-01&to=2026-01-01')
    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Başlangıç tarihi bitiş tarihinden sonra olamaz.')
    expect(alert.closest('section')).toContainElement(screen.getByLabelText('Oluşturulma sonu'))
    expect(getBackups).not.toHaveBeenCalled()
    expect(screen.queryByText(/Veriler alınamadı/)).not.toBeInTheDocument()
    fireEvent.change(screen.getByLabelText('Oluşturulma başlangıcı'), { target: { value: '2026-01-01' } })
    expect(await screen.findAllByRole('row')).not.toHaveLength(0)
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    expect(getBackups).toHaveBeenCalledTimes(1)
  })
})
