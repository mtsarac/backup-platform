import { beforeEach, describe, expect, it, vi } from 'vitest'
import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import * as api from '@/features/platform/api'
import { makePlatformAgent, paged, renderPlatform } from '@/features/platform/test-utils'
import { ApiError } from '@/lib/api/client'
import { PlatformAgentsPage } from './platform-agents-page'

vi.mock('@/features/platform/api')

beforeEach(() => {
  vi.resetAllMocks()
  vi.mocked(api.getPlatformAgents).mockResolvedValue(paged([
    makePlatformAgent(),
    makePlatformAgent({ tenantId: 'tenant-b', tenantName: 'Globex', agentId: 'agent-b', hostName: 'SQL-02', status: 'Revoked', connectivity: 'offline', platform: 'Linux', osDescription: null, agentVersion: null, protocolVersion: null, capabilityCount: 0, capabilitiesReportedAt: null, lastSeenAt: null }),
  ]))
})

describe('platform global agents list', () => {
  it('shows tenant, lifecycle, connectivity, OS, versions, capabilities and last seen', async () => {
    renderPlatform(<PlatformAgentsPage />)
    const first = (await screen.findByRole('link', { name: 'SQL-01' })).closest('tr')!
    expect(within(first).getByRole('link', { name: 'Acme A.Ş.' })).toHaveAttribute('href', '/platform/tenants/tenant-a')
    expect(screen.getByRole('link', { name: 'SQL-01' })).toHaveAttribute('href', '/platform/tenants/tenant-a/agents/agent-a')
    expect(within(first).getByText('Etkin')).toBeInTheDocument()
    expect(within(first).getByText('Çevrimiçi')).toBeInTheDocument()
    expect(within(first).getByText('Windows Server 2025')).toBeInTheDocument()
    expect(within(first).getByText('1.4.0')).toBeInTheDocument()
    expect(within(first).getByText('Protokol: 1.0')).toBeInTheDocument()
    expect(within(first).getByText('3 yetenek')).toBeInTheDocument()
    expect(within(first).getByText(new Date('2026-10-06T12:00:00Z').toLocaleString('tr-TR'))).toBeInTheDocument()
    expect(within(first).queryByText('Henüz bildirilmedi')).not.toBeInTheDocument()

    const second = screen.getByRole('link', { name: 'SQL-02' }).closest('tr')!
    expect(within(second).getByText('İptal edildi')).toBeInTheDocument()
    expect(within(second).getByText('Çevrimdışı')).toBeInTheDocument()
    // capabilities never reported, and last seen never recorded
    expect(within(second).getAllByText('Henüz bildirilmedi')).toHaveLength(2)
    expect(screen.queryByRole('button', { name: /iptal|devre dışı bırak/i })).not.toBeInTheDocument()
  })

  it('passes tenant, connectivity, lifecycle and search filters from the URL to the API', async () => {
    renderPlatform(<PlatformAgentsPage />, { path: '/platform/agents?tenantId=tenant-a&connectivity=offline&status=Revoked&search=sql&page=2' })
    await screen.findByRole('link', { name: 'SQL-01' })
    expect(api.getPlatformAgents).toHaveBeenCalledWith({ tenantId: 'tenant-a', connectivity: 'offline', status: 'Revoked', search: 'sql', page: 2, pageSize: 25 }, expect.any(AbortSignal))
    expect(screen.getByRole('combobox', { name: 'Bağlantı' })).toHaveValue('offline')
    expect(screen.getByRole('combobox', { name: 'Yaşam döngüsü' })).toHaveValue('Revoked')
  })

  it('changing a filter updates the URL and resets the page', async () => {
    renderPlatform(<PlatformAgentsPage />, { path: '/platform/agents?page=2' })
    await screen.findByRole('link', { name: 'SQL-01' })
    fireEvent.change(screen.getByRole('combobox', { name: 'Bağlantı' }), { target: { value: 'online' } })
    await waitFor(() => expect(screen.getByTestId('location')).toHaveTextContent('/platform/agents?connectivity=online'))
    expect(api.getPlatformAgents).toHaveBeenLastCalledWith({ tenantId: '', connectivity: 'online', status: undefined, search: '', page: 1, pageSize: 25 }, expect.any(AbortSignal))
  })

  it('renders the empty state for filters without results', async () => {
    vi.mocked(api.getPlatformAgents).mockResolvedValue(paged([]))
    renderPlatform(<PlatformAgentsPage />, { path: '/platform/agents?connectivity=unknown' })
    expect(await screen.findByText("Filtrelere uygun ajan bulunamadı.")).toBeInTheDocument()
  })

  it('renders the authorization state for 403', async () => {
    vi.mocked(api.getPlatformAgents).mockRejectedValue(new ApiError(403))
    renderPlatform(<PlatformAgentsPage />)
    expect(await screen.findByRole('alert')).toHaveTextContent('Bu alan için yetkiniz yok')
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
  })

  it('paginates', async () => {
    vi.mocked(api.getPlatformAgents).mockResolvedValue(paged([makePlatformAgent()], { totalCount: 30 }))
    renderPlatform(<PlatformAgentsPage />, { path: '/platform/agents' })
    expect(await screen.findByText('30 kayıt · Sayfa 1 / 2')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Önceki' })).toBeDisabled()
    fireEvent.click(screen.getByRole('button', { name: 'Sonraki' }))
    await waitFor(() => expect(screen.getByTestId('location')).toHaveTextContent('/platform/agents?page=2'))
  })
})
