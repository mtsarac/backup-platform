import { fireEvent, screen, within } from '@testing-library/react'
import { render } from '@/test/render'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { beforeEach, expect, it, vi } from 'vitest'
import { getAgents } from '@/features/agents/api'
import type { Agent } from '@/features/agents/types'
import { LocationProbe } from '@/features/platform/location-probe'
import { setViewport } from '@/test/viewport'
import { ClientsPage } from './clients-page'

vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => ({ tenantId: 'tenant-a', tenant: { name: 'Acme' } }) }))
vi.mock('@/features/agents/api')

/** A computer enrolled on 2026-10-<day>. */
function makeAgent(id: string, name: string, day: number, patch: Partial<Agent> = {}): Agent {
  return { agentId: id, hostName: name, status: 'Active', connectivity: 'online', operationalStatus: 'Online', lastSeenAt: `2026-11-${String(day).padStart(2, '0')}T08:00:00Z`, lastHeartbeatAt: null, lastConnectedAt: null, platform: 'Windows', agentVersion: '1', protocolVersion: '1', capabilityRevision: null, capabilities: [], enrolledAt: `2026-10-${String(day).padStart(2, '0')}T08:00:00Z`, ...patch }
}

beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(getAgents).mockResolvedValue([makeAgent('a', 'Muhasebe-PC', 1), makeAgent('b', 'Satış-PC', 2, { connectivity: 'offline' }), makeAgent('c', 'Depo-PC', 3, { connectivity: 'unknown' })])
})

function mount(url = '/tenants/tenant-a/agents') {
  const router = createMemoryRouter([{ path: '/tenants/:tenantId/agents', element: <><ClientsPage /><LocationProbe /></> }], { initialEntries: [url] })
  return render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}><RouterProvider router={router} /></QueryClientProvider>)
}
const names = () => screen.getAllByRole('row').slice(1).map(row => within(row).getAllByRole('cell')[0].textContent)
const location = () => screen.getByTestId('location')
const header = (name: string) => screen.getByRole('columnheader', { name })
async function ready() { await screen.findByRole('table') }

it('lists the newest enrolled computer first until another order is chosen', async () => {
  mount()
  await ready()
  expect(names()).toEqual(['Depo-PC', 'Satış-PC', 'Muhasebe-PC'])
  expect(header('Eklenme')).toHaveAttribute('aria-sort', 'descending')
  expect(location()).toHaveTextContent(/^\/tenants\/tenant-a\/agents$/)
})

it('sorts by a header: name A to Z first, then Z to A, in the URL; the default order drops out of the URL', async () => {
  mount()
  await ready()
  fireEvent.click(within(header('Bilgisayar')).getByRole('button'))
  expect(names()).toEqual(['Depo-PC', 'Muhasebe-PC', 'Satış-PC'])
  expect(location()).toHaveTextContent('sort=name&dir=asc')
  fireEvent.click(within(header('Bilgisayar')).getByRole('button'))
  expect(names()).toEqual(['Satış-PC', 'Muhasebe-PC', 'Depo-PC'])
  expect(location()).toHaveTextContent('sort=name&dir=desc')
  fireEvent.click(within(header('Eklenme')).getByRole('button'))
  expect(location()).toHaveTextContent(/^\/tenants\/tenant-a\/agents$/)
})

it('sorts by status (online first) and by last seen (newest first), restoring the order from the URL', async () => {
  mount('/tenants/tenant-a/agents?sort=status&dir=asc')
  await ready()
  expect(names()).toEqual(['Muhasebe-PC', 'Satış-PC', 'Depo-PC'])
  fireEvent.click(within(header('Son görülme')).getByRole('button'))
  expect(names()).toEqual(['Depo-PC', 'Satış-PC', 'Muhasebe-PC'])
  expect(location()).toHaveTextContent('sort=lastSeen&dir=desc')
})

it('puts computers that never connected or were never enrolled at the end, in either direction', async () => {
  vi.mocked(getAgents).mockResolvedValue([makeAgent('a', 'A', 1), makeAgent('n', 'Yeni', 2, { lastSeenAt: null }), makeAgent('b', 'B', 3)])
  mount('/tenants/tenant-a/agents?sort=lastSeen&dir=asc')
  await ready()
  expect(names()).toEqual(['A', 'B', 'Yeni'])
  fireEvent.click(within(header('Son görülme')).getByRole('button'))
  expect(names()).toEqual(['B', 'A', 'Yeni'])
})

it('ignores an unknown sort in the URL', async () => {
  mount('/tenants/tenant-a/agents?sort=nonsense&dir=sideways')
  await ready()
  expect(names()).toEqual(['Depo-PC', 'Satış-PC', 'Muhasebe-PC'])
})

it('searches by name; the search lives in the URL', async () => {
  mount()
  await ready()
  fireEvent.change(screen.getByRole('searchbox', { name: 'Bilgisayar ara' }), { target: { value: 'sat' } })
  expect(names()).toEqual(['Satış-PC'])
  expect(location()).toHaveTextContent('search=sat')
})

it('filters by connection state; the filter lives in the URL', async () => {
  mount()
  await ready()
  fireEvent.change(screen.getByRole('combobox', { name: 'Durum' }), { target: { value: 'offline' } })
  expect(names()).toEqual(['Satış-PC'])
  expect(location()).toHaveTextContent('connectivity=offline')
})

it('asks for revoked computers only while the switch is on, and keeps the switch in the URL', async () => {
  vi.mocked(getAgents).mockImplementation(async (_tenant, _signal, includeRevoked) => includeRevoked ? [makeAgent('r', 'Eski-PC', 4, { status: 'Revoked' }), makeAgent('a', 'Muhasebe-PC', 1)] : [makeAgent('a', 'Muhasebe-PC', 1)])
  mount()
  await ready()
  fireEvent.click(screen.getByRole('checkbox', { name: 'İptal edilenleri göster' }))
  expect(await screen.findByRole('link', { name: 'Eski-PC' })).toBeInTheDocument()
  expect(location()).toHaveTextContent('revoked=true')
  expect(getAgents).toHaveBeenLastCalledWith('tenant-a', expect.any(AbortSignal), true)
})

it('filters that match nothing say so and offer to clear them, keeping the sort', async () => {
  mount('/tenants/tenant-a/agents?search=yok&connectivity=online&sort=name&dir=asc')
  expect(await screen.findByText('Filtrelere uygun bilgisayar bulunamadı.')).toBeInTheDocument()
  expect(screen.getAllByRole('button', { name: 'Filtreleri temizle' })).toHaveLength(1)
  fireEvent.click(screen.getByRole('button', { name: 'Filtreleri temizle' }))
  expect(location()).toHaveTextContent(/^\/tenants\/tenant-a\/agents\?sort=name&dir=asc$/)
  expect(await screen.findByRole('link', { name: 'Depo-PC' })).toBeInTheDocument()
})

it('a tenant without computers sees the one action to add the first, and no filters', async () => {
  vi.mocked(getAgents).mockResolvedValue([])
  mount()
  expect(await screen.findByText('Henüz yedekleme bilgisayarı eklenmedi.')).toBeInTheDocument()
  expect(screen.getAllByRole('button', { name: 'Yedekleme bilgisayarı ekle' })).toHaveLength(1)
  expect(screen.queryByRole('searchbox')).not.toBeInTheDocument()
})

it('on a narrow screen the computers are cards and a menu sorts them', async () => {
  setViewport(false)
  mount()
  const sort = await screen.findByRole('combobox', { name: 'Sırala' })
  expect(sort).toHaveValue('enrolled:desc')
  const titles = () => screen.getAllByRole('heading', { level: 2 }).map(item => item.textContent)
  expect(titles()).toEqual(['Depo-PC', 'Satış-PC', 'Muhasebe-PC'])
  fireEvent.change(sort, { target: { value: 'name:asc' } })
  expect(titles()).toEqual(['Depo-PC', 'Muhasebe-PC', 'Satış-PC'])
  expect(location()).toHaveTextContent('sort=name&dir=asc')
})
