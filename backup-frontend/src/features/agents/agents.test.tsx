/// <reference lib="es2024.promise" />
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import type { ReactNode } from 'react'
import { ClientsPage } from '@/pages/clients/clients-page'
import { ClientDetailPage } from '@/pages/clients/client-detail-page'
import { AgentRegisterPage } from '@/pages/clients/agent-register-page'
import { CreateTokenDialog } from './create-token-dialog'
import type { AgentDetail, CreatedEnrollmentToken } from './types'
import * as api from './api'

const tenancy = vi.hoisted(() => ({ tenantId: 'tenant-a', canAdmin: true, canOperate: true, tenant: { tenantId: 'tenant-a', name: 'Test', role: 'Owner' } }))
vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => tenancy }))
vi.mock('./api', () => ({ getAgents: vi.fn(), getAgent: vi.fn(), revokeAgent: vi.fn(), getEnrollmentPolicy: vi.fn(), setEnrollmentPolicy: vi.fn(), getEnrollmentTokens: vi.fn(), createEnrollmentToken: vi.fn(), revokeEnrollmentToken: vi.fn() }))

const agent: AgentDetail = {
  agentId: 'agent-a', hostName: 'SQL-01', platform: 'Windows', status: 'Active',
  connectivity: 'online', operationalStatus: 'Online', agentVersion: '1.2.0', protocolVersion: '1.0',
  lastSeenAt: '2026-10-06T12:00:00Z', lastHeartbeatAt: null, lastConnectedAt: null,
  capabilityRevision: '2', capabilities: [{ identifier: 'backup.mssql', support: 'Supported', version: '1.0' }],
  runtimeVersion: '.NET 10', osDescription: 'Windows Server', enrolledAt: '2026-10-01T10:00:00Z',
}

function mount(content: ReactNode, path = '/tenants/tenant-a/agents') {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  const view = render(<QueryClientProvider client={client}><MemoryRouter initialEntries={[path]}><Routes><Route path="/tenants/:tenantId/agents/:id" element={content} /><Route path="*" element={content} /></Routes></MemoryRouter></QueryClientProvider>)
  return { client, ...view }
}

beforeEach(() => {
  vi.resetAllMocks()
  Object.assign(tenancy, { tenantId: 'tenant-a', canAdmin: true, canOperate: true })
  tenancy.tenant.role = 'Owner'
  vi.mocked(api.getAgents).mockResolvedValue([agent])
  vi.mocked(api.getAgent).mockResolvedValue(agent)
  vi.mocked(api.getEnrollmentPolicy).mockResolvedValue({ tenantId: 'tenant-a', enabled: true })
  vi.mocked(api.getEnrollmentTokens).mockResolvedValue([{ tokenId: 'token-a', createdAt: '2026-10-06T10:00:00Z', expiresAt: '2026-10-07T10:00:00Z', createdBy: 'internal-user-id' }])
  vi.mocked(api.createEnrollmentToken).mockResolvedValue({ tokenId: 'token-b', token: 'secret-once-only', expiresAt: '2026-10-07T10:00:00Z' })
  vi.mocked(api.revokeAgent).mockResolvedValue(undefined)
  vi.mocked(api.revokeEnrollmentToken).mockResolvedValue(undefined)
  vi.mocked(api.setEnrollmentPolicy).mockResolvedValue({ tenantId: 'tenant-a', enabled: false })
})

describe('Agent read views', () => {
  it('keeps revoked and disabled lifecycle separate from offline connectivity', async () => {
    vi.mocked(api.getAgents).mockResolvedValue([
      { ...agent, hostName: 'Revoked host', status: 'Revoked', operationalStatus: 'Revoked', connectivity: 'offline' },
      { ...agent, agentId: 'agent-b', hostName: 'Disabled host', status: 'Disabled', operationalStatus: 'Disabled', connectivity: 'online' },
    ])
    mount(<ClientsPage />)
    const revoked = (await screen.findByRole('link', { name: 'Revoked host' })).closest('tr')!
    expect(within(revoked).getAllByText('İptal edildi')).toHaveLength(2)
    expect(within(revoked).getByText('Çevrimdışı')).toBeInTheDocument()
    const disabled = screen.getByRole('link', { name: 'Disabled host' }).closest('tr')!
    expect(within(disabled).getAllByText('Devre dışı')).toHaveLength(2)
    expect(within(disabled).getByText('Çevrimiçi')).toBeInTheDocument()
    expect(api.getAgents).toHaveBeenCalledWith('tenant-a', expect.any(AbortSignal))
    expect(screen.getByRole('link', { name: 'Revoked host' })).toHaveAttribute('href', '/tenants/tenant-a/agents/agent-a')
  })

  it.each(['Viewer', 'Operator'])('%s can read safe detail but cannot revoke', async (role) => {
    tenancy.canAdmin = false
    tenancy.tenant.role = role
    mount(<ClientDetailPage />, '/tenants/tenant-a/agents/agent-a')
    expect(await screen.findByRole('heading', { name: 'SQL-01' })).toBeInTheDocument()
    expect(screen.getByText('.NET 10')).toBeInTheDocument()
    expect(screen.getByText('Windows Server')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Agent kaydını iptal et' })).not.toBeInTheDocument()
    expect(screen.queryByText('agent-a')).not.toBeInTheDocument()
    expect(api.getAgent).toHaveBeenCalledWith('tenant-a', 'agent-a', expect.any(AbortSignal))
  })

  it('lists persisted capabilities with known names and separate supported/unsupported/unknown states', async () => {
    const capabilities = [
      { identifier: 'mssql.backup.full', support: 'Supported', metadata: { scope: 'agent' } },
      { identifier: 'mssql.backup.checksum', support: 'Supported' },
      { identifier: 'mssql.backup.differential', support: 'Unsupported' },
      { identifier: 'vendor.custom-feature', support: 'Unknown' },
    ]
    vi.mocked(api.getAgents).mockResolvedValue([{ ...agent, capabilities }])
    vi.mocked(api.getAgent).mockResolvedValue({ ...agent, capabilities, capabilitiesRevision: 'c1-abc', capabilitiesReportedAt: '2026-10-06T12:00:00Z' })
    const list = mount(<ClientsPage />)
    const row = (await screen.findByRole('link', { name: 'SQL-01' })).closest('tr')!
    expect(within(row).getByText('MSSQL Full Backup')).toBeInTheDocument()
    expect(within(row).getByText('CHECKSUM')).toBeInTheDocument()
    expect(within(row).getByText('2 yetenek desteklenmiyor veya bilinmiyor')).toBeInTheDocument()
    list.unmount()
    mount(<ClientDetailPage />, '/tenants/tenant-a/agents/agent-a')
    const section = (await screen.findByRole('heading', { name: 'Bildirilen yetenekler' })).closest('section')!
    expect(within(section).getAllByText('Destekleniyor')).toHaveLength(2)
    expect(within(section).getByText('Desteklenmiyor')).toBeInTheDocument()
    expect(within(section).getByText('Bilinmiyor')).toBeInTheDocument()
    expect(within(section).getByText('vendor.custom-feature')).toBeInTheDocument()
    expect(screen.getByText('c1-abc')).toBeInTheDocument()
  })

  it('says capabilities were not reported yet instead of inventing support', async () => {
    vi.mocked(api.getAgents).mockResolvedValue([{ ...agent, capabilities: [] }])
    mount(<ClientsPage />)
    const row = (await screen.findByRole('link', { name: 'SQL-01' })).closest('tr')!
    expect(within(row).getByText('Henüz bildirilmedi')).toBeInTheDocument()
  })

  it('requires focused confirmation before admin revocation and invalidates only related scopes', async () => {
    const { client } = mount(<ClientDetailPage />, '/tenants/tenant-a/agents/agent-a')
    const invalidate = vi.spyOn(client, 'invalidateQueries')
    fireEvent.click(await screen.findByRole('button', { name: 'Agent kaydını iptal et' }))
    const dialog = await screen.findByRole('dialog')
    await waitFor(() => expect(within(dialog).getByRole('button', { name: 'Vazgeç' })).toHaveFocus())
    expect(api.revokeAgent).not.toHaveBeenCalled()
    fireEvent.click(within(dialog).getByRole('button', { name: 'İptali onayla' }))
    await waitFor(() => expect(api.revokeAgent).toHaveBeenCalledWith('tenant-a', 'agent-a'))
    await waitFor(() => expect(invalidate).toHaveBeenCalledWith({ queryKey: ['tenant', 'tenant-a', 'agents'] }))
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['tenant', 'tenant-a', 'dashboard'] })
  })
})

describe('Enrollment', () => {
  it.each(['Viewer', 'Operator'])('%s cannot query or mutate enrollment administration', (role) => {
    tenancy.canAdmin = false
    tenancy.tenant.role = role
    mount(<AgentRegisterPage />)
    expect(screen.getByRole('alert')).toHaveTextContent('yöneticisi')
    expect(api.getEnrollmentPolicy).not.toHaveBeenCalled()
    expect(api.getEnrollmentTokens).not.toHaveBeenCalled()
    expect(screen.queryByRole('button', { name: 'Token oluştur' })).not.toBeInTheDocument()
  })

  it('shows policy off and disables token creation, while admin may enable enrollment', async () => {
    vi.mocked(api.getEnrollmentPolicy).mockResolvedValue({ tenantId: 'tenant-a', enabled: false })
    mount(<AgentRegisterPage />)
    const toggle = await screen.findByRole('switch', { name: 'Yeni agent kayıtlarına izin ver' })
    expect(toggle).toHaveAttribute('aria-checked', 'false')
    expect(screen.getByRole('button', { name: 'Token oluştur' })).toBeDisabled()
    fireEvent.click(toggle)
    await waitFor(() => expect(api.setEnrollmentPolicy).toHaveBeenCalledWith('tenant-a', true))
  })

  it('shows plaintext once without query or mutation cache persistence, and clears it on close', async () => {
    const { client } = mount(<AgentRegisterPage />)
    await screen.findByRole('switch')
    fireEvent.click(screen.getByRole('button', { name: 'Token oluştur' }))
    const dialog = await screen.findByRole('dialog')
    const lifetime = within(dialog).getByRole('spinbutton', { name: 'Geçerlilik süresi (dakika)' })
    fireEvent.change(lifetime, { target: { value: '60' } })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Token oluştur' }))
    expect(await screen.findByDisplayValue('secret-once-only')).toBeInTheDocument()
    expect(api.createEnrollmentToken).toHaveBeenCalledWith('tenant-a', 60, expect.any(AbortSignal))
    expect(within(dialog).getByText('Bu token tekrar görüntülenemez.')).toBeInTheDocument()
    expect(JSON.stringify(client.getQueryCache().getAll().map((query) => query.state.data))).not.toContain('secret-once-only')
    expect(client.getMutationCache().getAll()).toHaveLength(0)
    expect(screen.queryByText('internal-user-id')).not.toBeInTheDocument()
    expect(screen.queryByText('token-a')).not.toBeInTheDocument()
    fireEvent.click(within(dialog).getByRole('button', { name: 'Kapat' }))
    await waitFor(() => expect(screen.queryByDisplayValue('secret-once-only')).not.toBeInTheDocument())
    fireEvent.click(screen.getByRole('button', { name: 'Token oluştur' }))
    expect(await screen.findByRole('spinbutton')).toHaveValue(1440)
    expect(screen.queryByDisplayValue('secret-once-only')).not.toBeInTheDocument()
  })

  it('revokes an active token only after confirmation', async () => {
    const { client } = mount(<AgentRegisterPage />)
    const invalidate = vi.spyOn(client, 'invalidateQueries')
    fireEvent.click(await screen.findByRole('button', { name: /tarihinde oluşturulan token'ı iptal et/ }))
    const dialog = await screen.findByRole('dialog')
    expect(api.revokeEnrollmentToken).not.toHaveBeenCalled()
    fireEvent.click(within(dialog).getByRole('button', { name: 'İptali onayla' }))
    await waitFor(() => expect(api.revokeEnrollmentToken).toHaveBeenCalledWith('tenant-a', 'token-a'))
    await waitFor(() => expect(invalidate).toHaveBeenCalledWith({ queryKey: ['tenant', 'tenant-a', 'agent-enrollment-tokens'] }))
  })

  it('aborts token issuance on unmount and ignores late plaintext responses', async () => {
    const { promise, resolve } = Promise.withResolvers<CreatedEnrollmentToken>()
    vi.mocked(api.createEnrollmentToken).mockReturnValue(promise)
    const onCreated = vi.fn()
    const view = mount(<CreateTokenDialog tenantId="tenant-a" onClose={vi.fn()} onCreated={onCreated} />)
    fireEvent.click(await screen.findByRole('button', { name: 'Token oluştur' }))
    const signal = vi.mocked(api.createEnrollmentToken).mock.calls[0]![2]
    view.unmount()
    expect(signal.aborted).toBe(true)
    await act(async () => { resolve({ tokenId: 'token-b', token: 'late-secret', expiresAt: '2026-10-07T10:00:00Z' }) })
    expect(onCreated).not.toHaveBeenCalled()
  })

  it('clears plaintext when the tenant changes without a route unmount', async () => {
    const { client, rerender } = mount(<AgentRegisterPage />)
    await screen.findByRole('switch')
    fireEvent.click(screen.getByRole('button', { name: 'Token oluştur' }))
    const dialog = await screen.findByRole('dialog')
    fireEvent.click(within(dialog).getByRole('button', { name: 'Token oluştur' }))
    await screen.findByDisplayValue('secret-once-only')
    tenancy.tenantId = 'tenant-b'
    rerender(<QueryClientProvider client={client}><MemoryRouter initialEntries={['/tenants/tenant-a/agents']}><Routes><Route path="/tenants/:tenantId/agents/:id" element={<AgentRegisterPage />} /><Route path="*" element={<AgentRegisterPage />} /></Routes></MemoryRouter></QueryClientProvider>)
    await waitFor(() => expect(screen.queryByDisplayValue('secret-once-only')).not.toBeInTheDocument())
    expect(api.getEnrollmentPolicy).toHaveBeenCalledWith('tenant-b', expect.any(AbortSignal))
  })
})
