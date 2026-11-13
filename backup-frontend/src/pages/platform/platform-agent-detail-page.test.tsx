import { beforeEach, describe, expect, it, vi } from 'vitest'
import { screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import * as api from '@/features/platform/api'
import { makePlatformAgent, renderPlatform } from '@/features/platform/test-utils'
import type { PlatformAgentDetail } from '@/features/platform/types'
import { ApiError } from '@/lib/api/client'
import { PlatformAgentDetailPage } from './platform-agent-detail-page'

vi.mock('@/features/platform/api')

const route = '/platform/tenants/:tenantId/agents/:agentId'
const path = '/platform/tenants/tenant-a/agents/agent-a'
const threeHoursAgo = () => new Date(Date.now() - 3 * 3_600_000).toISOString()

function detail(overrides: Partial<PlatformAgentDetail> = {}): PlatformAgentDetail {
  return { ...makePlatformAgent(), lastHeartbeatAt: '2026-10-06T11:59:00Z', lastConnectedAt: '2026-10-06T09:00:00Z', runtimeVersion: '.NET 10', capabilities: [{ identifier: 'vendor.custom-feature', support: 'Supported' }, { identifier: 'vendor.other', support: 'Unsupported' }], ...overrides }
}

beforeEach(() => {
  vi.resetAllMocks()
  vi.mocked(api.getPlatformAgent).mockResolvedValue(detail({ lastSeenAt: threeHoursAgo() }))
})

describe('platform backup computer page', () => {
  it('leads with the connection state, last seen and the workspace, without tabs', async () => {
    renderPlatform(<PlatformAgentDetailPage />, { path, route })
    expect(await screen.findByRole('heading', { level: 1, name: 'SQL-01' })).toBeInTheDocument()
    expect(screen.getByText('Çevrimiçi')).toBeVisible()
    expect(screen.getByText('3 saat önce')).toBeVisible()
    expect(screen.getByRole('link', { name: 'Acme A.Ş.' })).toHaveAttribute('href', '/platform/tenants/tenant-a')
    expect(screen.queryByRole('tab')).not.toBeInTheDocument()
    expect(api.getPlatformAgent).toHaveBeenCalledWith('tenant-a', 'agent-a', expect.any(AbortSignal))
  })

  it('keeps lifecycle, platform, versions, identifiers and capabilities in collapsed, copyable technical details', async () => {
    renderPlatform(<PlatformAgentDetailPage />, { path, route })
    await screen.findByRole('heading', { level: 1, name: 'SQL-01' })
    for (const value of ['agent-a', 'Windows Server 2025', '1.4.0', '.NET 10', 'rev-3', 'vendor.custom-feature']) expect(screen.queryByText(value)).not.toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Teknik ayrıntılar' }))
    const technical = within(await screen.findByRole('dialog'))
    for (const value of ['agent-a', 'Windows Server 2025', '1.4.0', '.NET 10', 'rev-3', 'vendor.custom-feature']) expect(technical.getByText(value)).toBeVisible()
    expect(technical.getByRole('button', { name: 'Kopyala: Bilgisayar kimliği' })).toBeInTheDocument()
  })

  it('shows a revoked lifecycle beside the offline connection instead of folding them together', async () => {
    vi.mocked(api.getPlatformAgent).mockResolvedValue(detail({ status: 'Revoked', connectivity: 'offline', lastSeenAt: null }))
    renderPlatform(<PlatformAgentDetailPage />, { path, route })
    await screen.findByRole('heading', { level: 1, name: 'SQL-01' })
    expect(screen.getByText('İptal edildi')).toBeVisible()
    expect(screen.getByText('Çevrimdışı')).toBeVisible()
    expect(screen.getByText(/Henüz bağlanmadı/)).toBeVisible()
  })

  it('says capabilities were not reported instead of implying support', async () => {
    vi.mocked(api.getPlatformAgent).mockResolvedValue(detail({ capabilities: [], capabilitiesReportedAt: null }))
    renderPlatform(<PlatformAgentDetailPage />, { path, route })
    await screen.findByRole('heading', { level: 1, name: 'SQL-01' })
    await userEvent.click(screen.getByRole('button', { name: 'Teknik ayrıntılar' }))
    expect(within(await screen.findByRole('dialog')).getByText('Yetenekler henüz bildirilmedi.')).toBeVisible()
  })

  it('shows the authorization state for 403', async () => {
    vi.mocked(api.getPlatformAgent).mockRejectedValue(new ApiError(403))
    renderPlatform(<PlatformAgentDetailPage />, { path, route })
    expect(await screen.findByRole('alert')).toHaveTextContent('Bu alan için yetkiniz yok')
  })
})
