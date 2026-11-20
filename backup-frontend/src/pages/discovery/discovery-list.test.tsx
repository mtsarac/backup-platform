import { fireEvent, screen, within } from '@testing-library/react'
import { render } from '@/test/render'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { beforeEach, expect, it, vi } from 'vitest'
import { getAgents } from '@/features/agents/api'
import { getDiscoveryAgents, getDiscoveryInstances } from '@/features/discovery/api'
import { makeInstance, makeStatus, supportedAgent } from '@/features/discovery/test-fixtures'
import type { DiscoveryInstanceSummary } from '@/features/discovery/types'
import { LocationProbe } from '@/features/platform/location-probe'
import { setViewport } from '@/test/viewport'
import { DiscoveryPage } from './discovery-page'
import { engineLabels } from '@/features/discovery/status'

vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => ({ tenantId: 'tenant-a', tenant: { name: 'Acme' } }) }))
vi.mock('@/features/discovery/api')
vi.mock('@/features/agents/api')

const second = { ...supportedAgent, agentId: 'agent-b', hostName: 'Depo Agent' }
const otherEngine = Object.keys(engineLabels).find(key => key !== 'mssql') as DiscoveryInstanceSummary['engine']
/** A server first found on 2026-10-<day>. */
const server = (id: string, name: string, day: number, patch: Partial<DiscoveryInstanceSummary> = {}) => makeInstance({ id, displayName: name, firstSeenAt: `2026-10-${String(day).padStart(2, '0')}T08:00:00Z`, ...patch })

beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(getAgents).mockResolvedValue([supportedAgent, second] as never)
  vi.mocked(getDiscoveryAgents).mockResolvedValue({ items: [makeStatus(), makeStatus({ agentId: 'agent-b' })] })
  vi.mocked(getDiscoveryInstances).mockResolvedValue({ items: [
    server('s1', 'Muhasebe', 1),
    server('s2', 'Satış', 2, { agentId: 'agent-b', access: 'CredentialsRequired', databaseCounts: { total: 1, user: 1, backupEligible: 7 } }),
    server('s3', 'Depo', 3, { engine: otherEngine, databaseCounts: { total: 9, user: 9, backupEligible: 9 } }),
  ] })
})

function mount(url = '/tenants/tenant-a/discovery') {
  const router = createMemoryRouter([{ path: '/tenants/:tenantId/discovery', element: <><DiscoveryPage /><LocationProbe /></> }], { initialEntries: [url] })
  return render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}><RouterProvider router={router} /></QueryClientProvider>)
}
const names = () => screen.getAllByRole('row').slice(1).map(row => within(row).getAllByRole('cell')[0].textContent)
const location = () => screen.getByTestId('location')
const header = (name: string) => screen.getByRole('columnheader', { name })
async function ready() { await screen.findByRole('table') }

it('lists the newest found server first until another order is chosen', async () => {
  mount()
  await ready()
  expect(names()).toEqual(['Depo', 'Satış', 'Muhasebe'])
  expect(header('İlk bulunma')).toHaveAttribute('aria-sort', 'descending')
  expect(location()).toHaveTextContent(/^\/tenants\/tenant-a\/discovery$/)
})

it('sorts by name, computer, eligible databases and last seen, the choice lives in the URL', async () => {
  mount()
  await ready()
  fireEvent.click(within(header('Veritabanı sunucusu')).getByRole('button'))
  expect(names()).toEqual(['Depo', 'Muhasebe', 'Satış'])
  expect(location()).toHaveTextContent('sort=name&dir=asc')
  fireEvent.click(within(header('Bilgisayar')).getByRole('button'))
  expect(names()).toEqual(['Satış', 'Depo', 'Muhasebe'])
  fireEvent.click(within(header('Yedeklenebilir veritabanı')).getByRole('button'))
  expect(location()).toHaveTextContent('sort=eligible&dir=desc')
  expect(names()).toEqual(['Depo', 'Muhasebe', 'Satış'])
  fireEvent.click(within(header('İlk bulunma')).getByRole('button'))
  expect(location()).toHaveTextContent(/^\/tenants\/tenant-a\/discovery$/)
})

it('puts servers without a working login last when sorting by eligible databases, in either direction', async () => {
  mount('/tenants/tenant-a/discovery?sort=eligible&dir=asc')
  await ready()
  expect(names()).toEqual(['Muhasebe', 'Depo', 'Satış'])
  fireEvent.click(within(header('Yedeklenebilir veritabanı')).getByRole('button'))
  expect(names()).toEqual(['Depo', 'Muhasebe', 'Satış'])
})

it('searches the server and the computer name; the search lives in the URL', async () => {
  mount()
  await ready()
  fireEvent.change(screen.getByRole('searchbox', { name: 'Sunucu veya bilgisayar ara' }), { target: { value: 'depo' } })
  expect(names()).toEqual(['Depo', 'Satış'])
  fireEvent.change(screen.getByRole('searchbox', { name: 'Sunucu veya bilgisayar ara' }), { target: { value: 'muhasebe' } })
  expect(names()).toEqual(['Muhasebe'])
  expect(location()).toHaveTextContent('search=muhasebe')
})

it('filters by access status among the statuses present', async () => {
  mount()
  await ready()
  const status = screen.getByRole('combobox', { name: 'Durum' })
  expect(within(status).getAllByRole('option').map(option => option.textContent)).toEqual(['Tümü', 'Kimlik bilgisi gerekli', 'Kimlik doğrulandı'])
  fireEvent.change(status, { target: { value: 'CredentialsRequired' } })
  expect(names()).toEqual(['Satış'])
  expect(location()).toHaveTextContent('access=CredentialsRequired')
})

it('filters by database type and by computer when there is more than one', async () => {
  mount()
  await ready()
  fireEvent.change(screen.getByRole('combobox', { name: 'Veritabanı türü' }), { target: { value: otherEngine } })
  expect(names()).toEqual(['Depo'])
  expect(location()).toHaveTextContent(`engine=${otherEngine}`)
  fireEvent.change(screen.getByRole('combobox', { name: 'Veritabanı türü' }), { target: { value: '' } })
  fireEvent.change(screen.getByRole('combobox', { name: 'Bilgisayar' }), { target: { value: 'agent-b' } })
  expect(names()).toEqual(['Satış'])
  expect(location()).toHaveTextContent('computer=agent-b')
})

it('offers no database type or computer filter when there is only one of each', async () => {
  vi.mocked(getDiscoveryInstances).mockResolvedValue({ items: [server('s1', 'Muhasebe', 1), server('s4', 'Ikinci', 2)] })
  mount()
  await ready()
  expect(screen.queryByRole('combobox', { name: 'Veritabanı türü' })).not.toBeInTheDocument()
  expect(screen.queryByRole('combobox', { name: 'Bilgisayar' })).not.toBeInTheDocument()
})

it('ignores a computer or type in the URL that the list does not offer', async () => {
  mount('/tenants/tenant-a/discovery?computer=gone&engine=nope')
  await ready()
  expect(names()).toEqual(['Depo', 'Satış', 'Muhasebe'])
})

it('filters that match nothing say so and offer to clear them, keeping the sort', async () => {
  mount('/tenants/tenant-a/discovery?search=yok&sort=name&dir=asc')
  expect(await screen.findByText('Aramanızla eşleşen sunucu yok.')).toBeInTheDocument()
  expect(screen.getAllByRole('button', { name: 'Filtreleri temizle' })).toHaveLength(1)
  fireEvent.click(screen.getByRole('button', { name: 'Filtreleri temizle' }))
  expect(location()).toHaveTextContent(/^\/tenants\/tenant-a\/discovery\?sort=name&dir=asc$/)
  expect(await screen.findByRole('link', { name: 'Depo' })).toBeInTheDocument()
})

it('without any server it keeps the plain empty state and shows no filters', async () => {
  vi.mocked(getDiscoveryInstances).mockResolvedValue({ items: [] })
  mount()
  expect(await screen.findByText(/henüz veritabanı bulunamadı/)).toBeInTheDocument()
  expect(screen.queryByRole('region', { name: 'Veritabanı bulma filtreleri' })).not.toBeInTheDocument()
})

it('on a narrow screen the servers are cards and a menu sorts them', async () => {
  setViewport(false)
  mount()
  const sort = await screen.findByRole('combobox', { name: 'Sırala' })
  expect(sort).toHaveValue('first:desc')
  const titles = () => within(screen.getByRole('list', { name: 'Bulunan veritabanı sunucuları' })).getAllByRole('heading', { level: 2 }).map(item => item.textContent)
  expect(titles()).toEqual(['Depo', 'Satış', 'Muhasebe'])
  fireEvent.change(sort, { target: { value: 'name:asc' } })
  expect(titles()).toEqual(['Depo', 'Muhasebe', 'Satış'])
})
