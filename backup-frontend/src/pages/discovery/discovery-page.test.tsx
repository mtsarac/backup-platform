import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { getAgents } from '@/features/agents/api'
import { getDiscoveryAgents, getDiscoveryInstances, rescanDiscoveryAgent } from '@/features/discovery/api'
import { legacyAgent, makeInstance, makeStatus, supportedAgent } from '@/features/discovery/test-fixtures'
import { ApiError } from '@/lib/api/client'
import { DiscoveryPage } from './discovery-page'

const access = vi.hoisted(() => ({ tenantId: 'tenant-a', tenant: { name: 'Acme' }, canOperate: true, canAdmin: true }))
vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => access }))
vi.mock('@/features/discovery/api')
vi.mock('@/features/agents/api')

function mount() {
  render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })}><MemoryRouter><DiscoveryPage /></MemoryRouter></QueryClientProvider>)
}
beforeEach(() => {
  vi.clearAllMocks()
  Object.assign(access, { canOperate: true, canAdmin: true })
  vi.mocked(getAgents).mockResolvedValue([supportedAgent, legacyAgent] as never)
  vi.mocked(getDiscoveryAgents).mockResolvedValue({ items: [makeStatus()] })
  vi.mocked(getDiscoveryInstances).mockResolvedValue({ items: [makeInstance(), makeInstance({ id: 'inst-2', displayName: 'Postgres', engine: 'postgres', access: 'CredentialsRequired', lastChange: { kind: 'Changed', at: '2026-10-02T08:00:00Z' } })] })
})

describe('discovery page', () => {
  it('lists instances with status pills and the Docker explanation', async () => {
    mount()
    expect(await screen.findByRole('link', { name: 'SQL Server (default)' })).toHaveAttribute('href', '/tenants/tenant-a/discovery/instances/inst-1')
    expect(screen.getByText('Kimlik bilgisi gerekli')).toBeInTheDocument()
    expect(screen.getByText('Değişti')).toBeInTheDocument()
    expect(screen.getByText('Yeni')).toBeInTheDocument()
    expect(screen.getByText(/Docker yok demek değildir/)).toBeInTheDocument()
  })
  it('disables rescan for an agent without the discovery capability and explains why', async () => {
    mount()
    expect(await screen.findByRole('button', { name: 'Old Agent yeniden tara' })).toBeDisabled()
    expect(screen.getByText(/Agent sürümü keşfi desteklemiyor/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'SQL Agent yeniden tara' })).toBeEnabled()
  })
  it('shows a friendly text for a throttled rescan', async () => {
    vi.mocked(rescanDiscoveryAgent).mockRejectedValue(new ApiError(429, { error: 'rescan_too_soon' }))
    mount()
    fireEvent.click(await screen.findByRole('button', { name: 'SQL Agent yeniden tara' }))
    expect(await screen.findByText(/Az önce tarama istendi/)).toBeInTheDocument()
    expect(rescanDiscoveryAgent).toHaveBeenCalledWith('tenant-a', 'agent-a')
  })
  it('hides every mutating control from a Viewer', async () => {
    Object.assign(access, { canOperate: false, canAdmin: false })
    mount()
    await screen.findByRole('link', { name: 'SQL Server (default)' })
    expect(screen.queryByRole('button', { name: /yeniden tara/ })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /keşif ayarları/ })).not.toBeInTheDocument()
  })
  it('shows settings only to administrators', async () => {
    mount()
    expect(await screen.findByRole('button', { name: 'SQL Agent keşif ayarları' })).toBeInTheDocument()
  })
  it('renders the empty state when there are no agents', async () => {
    vi.mocked(getAgents).mockResolvedValue([])
    mount()
    expect(await screen.findByText('Henüz Agent yok.')).toBeInTheDocument()
  })
  it('renders the unsupported state when no agent reports the capability', async () => {
    vi.mocked(getAgents).mockResolvedValue([legacyAgent] as never)
    mount()
    expect(await screen.findByText('Agent sürümü keşfi desteklemiyor.')).toBeInTheDocument()
  })
  it('renders the empty instance state', async () => {
    vi.mocked(getDiscoveryInstances).mockResolvedValue({ items: [] })
    mount()
    expect(await screen.findByText('Keşfedilen Instance yok.')).toBeInTheDocument()
  })
  it('renders a permission state on 403 and a generic error otherwise', async () => {
    vi.mocked(getDiscoveryInstances).mockRejectedValue(new ApiError(403))
    mount()
    expect(await screen.findByText(/görüntüleme yetkiniz yok/)).toBeInTheDocument()
  })
  it('renders an error for a failed load without backend text', async () => {
    vi.mocked(getDiscoveryInstances).mockRejectedValue(new ApiError(500, { error: 'secret stack' }))
    mount()
    expect(await screen.findByText(/Sunucu isteği tamamlayamadı/)).toBeInTheDocument()
    expect(screen.queryByText(/secret stack/)).not.toBeInTheDocument()
  })
  it('passes filters to the API', async () => {
    mount()
    await screen.findByRole('link', { name: 'SQL Server (default)' })
    fireEvent.change(screen.getByLabelText('Motor'), { target: { value: 'postgres' } })
    fireEvent.click(screen.getByLabelText('Kaybolanları göster'))
    await waitFor(() => expect(getDiscoveryInstances).toHaveBeenLastCalledWith('tenant-a', { engine: 'postgres', includeMissing: true }, expect.anything()))
  })
})
