/// <reference lib="es2024.promise" />
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { act, fireEvent, screen, waitFor, within } from '@testing-library/react'
import { render } from '@/test/render'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import type { ReactNode } from 'react'
import { ClientsPage } from '@/pages/clients/clients-page'
import { ClientDetailPage } from '@/pages/clients/client-detail-page'
import { AgentRegisterPage } from '@/pages/clients/agent-register-page'
import { AddAgentDialog } from './add-agent-dialog'
import type { AgentDetail, CreatedEnrollmentToken } from './types'
import * as api from './api'
import * as jobsApi from '@/features/backup-jobs/api'
import { setViewport } from '@/test/viewport'

const tenancy = vi.hoisted(() => ({ tenantId: 'tenant-a', canAdmin: true, canOperate: true, tenant: { tenantId: 'tenant-a', name: 'Test', role: 'Owner' } }))
vi.mock('@/features/backup-jobs/api', () => ({ getBackupJobs: vi.fn() }))
vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => tenancy }))
vi.mock('./api', () => ({ getAgentDistribution: vi.fn(), getAgents: vi.fn(), getAgent: vi.fn(), revokeAgent: vi.fn(), getEnrollmentPolicy: vi.fn(), setEnrollmentPolicy: vi.fn(), getEnrollmentTokens: vi.fn(), getEnrollmentTokenStatus: vi.fn(), createEnrollmentToken: vi.fn(), revokeEnrollmentToken: vi.fn() }))

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
  vi.mocked(api.getAgentDistribution).mockResolvedValue({ serverUrl: 'https://backend.example:7443', packages: { 'win-x64': true, 'linux-x64': true } })
  Object.assign(tenancy, { tenantId: 'tenant-a', canAdmin: true, canOperate: true })
  tenancy.tenant.role = 'Owner'
  vi.mocked(jobsApi.getBackupJobs).mockResolvedValue([])
  vi.mocked(api.getAgents).mockResolvedValue([agent])
  vi.mocked(api.getAgent).mockResolvedValue(agent)
  vi.mocked(api.getEnrollmentPolicy).mockResolvedValue({ tenantId: 'tenant-a', enabled: true })
  vi.mocked(api.getEnrollmentTokens).mockResolvedValue([{ tokenId: 'token-a', createdAt: '2026-10-06T10:00:00Z', expiresAt: '2026-10-07T10:00:00Z', createdBy: 'internal-user-id' }])
  const expiresAt = new Date(Date.now() + 60 * 60_000).toISOString()
  vi.mocked(api.createEnrollmentToken).mockResolvedValue({ tokenId: 'token-b', token: 'secret-once-only', expiresAt })
  vi.mocked(api.getEnrollmentTokenStatus).mockResolvedValue({ tokenId: 'token-b', expiresAt, state: 'Active', agentId: null })
  vi.mocked(api.revokeAgent).mockResolvedValue(undefined)
  vi.mocked(api.revokeEnrollmentToken).mockResolvedValue(undefined)
  vi.mocked(api.setEnrollmentPolicy).mockResolvedValue({ tenantId: 'tenant-a', enabled: false })
})

describe('Backup computers list', () => {
  it('leads with name, online state and last seen, and keeps platform, version, id and capabilities out of the list', async () => {
    const threeHoursAgo = new Date(Date.now() - 3 * 3_600_000).toISOString()
    vi.mocked(api.getAgents).mockResolvedValue([{ ...agent, lastSeenAt: threeHoursAgo }])
    mount(<ClientsPage />)
    expect(screen.getByRole('heading', { level: 1, name: 'Yedekleme bilgisayarları' })).toBeInTheDocument()
    const link = await screen.findByRole('link', { name: 'SQL-01' })
    expect(link).toHaveAttribute('href', '/tenants/tenant-a/agents/agent-a')
    const row = link.closest('tr')!
    expect(within(row).getByText('Çevrimiçi')).toBeInTheDocument()
    expect(within(row).getByText('3 saat önce')).toHaveAttribute('title', expect.stringContaining('2026'))
    for (const hidden of ['Windows', '1.2.0', 'agent-a', /yetenek/]) expect(within(row).queryByText(hidden)).not.toBeInTheDocument()
    expect(api.getAgents).toHaveBeenCalledWith('tenant-a', expect.any(AbortSignal))
  })

  it('keeps revoked and disabled lifecycle separate from offline connectivity', async () => {
    vi.mocked(api.getAgents).mockResolvedValue([
      { ...agent, hostName: 'Revoked host', status: 'Revoked', operationalStatus: 'Revoked', connectivity: 'offline' },
      { ...agent, agentId: 'agent-b', hostName: 'Disabled host', status: 'Disabled', operationalStatus: 'Disabled', connectivity: 'online' },
    ])
    mount(<ClientsPage />)
    const revoked = (await screen.findByRole('link', { name: 'Revoked host' })).closest('tr')!
    expect(within(revoked).getByText('İptal edildi')).toBeInTheDocument()
    expect(within(revoked).getByText('Çevrimdışı')).toBeInTheDocument()
    const disabled = screen.getByRole('link', { name: 'Disabled host' }).closest('tr')!
    expect(within(disabled).getByText('Devre dışı')).toBeInTheDocument()
    expect(within(disabled).getByText('Çevrimiçi')).toBeInTheDocument()
  })

  it('becomes cards on a phone with name, online state and last seen', async () => {
    setViewport(false)
    mount(<ClientsPage />)
    const card = (await screen.findByRole('link', { name: 'SQL-01' })).closest('li')!
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    expect(within(card).getByText('Çevrimiçi')).toBeInTheDocument()
    expect(within(card).getByText('Son görülme')).toBeInTheDocument()
    expect(within(card).queryByText('Windows')).not.toBeInTheDocument()
  })

  it('filters by name and connection state', async () => {
    vi.mocked(api.getAgents).mockResolvedValue([agent, { ...agent, agentId: 'agent-b', hostName: 'Depo-PC', connectivity: 'offline' }])
    mount(<ClientsPage />)
    await screen.findByRole('link', { name: 'Depo-PC' })
    fireEvent.change(screen.getByRole('searchbox', { name: 'Bilgisayar ara' }), { target: { value: 'sql' } })
    expect(screen.queryByRole('link', { name: 'Depo-PC' })).not.toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'SQL-01' })).toBeInTheDocument()
    fireEvent.change(screen.getByRole('searchbox', { name: 'Bilgisayar ara' }), { target: { value: '' } })
    fireEvent.change(screen.getByRole('combobox', { name: 'Durum' }), { target: { value: 'offline' } })
    expect(screen.queryByRole('link', { name: 'SQL-01' })).not.toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Depo-PC' })).toBeInTheDocument()
  })

  it('an administrator with no computers gets one primary action; others are told to ask their administrator', async () => {
    vi.mocked(api.getAgents).mockResolvedValue([])
    const admin = mount(<ClientsPage />)
    await screen.findByText(/Henüz yedekleme bilgisayarı eklenmedi/)
    expect(screen.getAllByRole('button', { name: 'Yedekleme bilgisayarı ekle' })).toHaveLength(1)
    admin.unmount()
    tenancy.canAdmin = false
    mount(<ClientsPage />)
    await screen.findByText(/Henüz yedekleme bilgisayarı eklenmedi/)
    expect(screen.getByText('Yöneticinize danışın')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Yedekleme bilgisayarı ekle' })).not.toBeInTheDocument()
  })

  it('does not offer the enrolment tabs to a non-administrator', async () => {
    tenancy.canAdmin = false
    mount(<ClientsPage />)
    await screen.findByRole('link', { name: 'SQL-01' })
    expect(screen.queryByRole('navigation', { name: 'Yedekleme bilgisayarı bölümleri' })).not.toBeInTheDocument()
  })
})

describe('Backup computer page', () => {
  it.each(['Viewer', 'Operator'])('%s can read the computer but cannot remove it', async (role) => {
    tenancy.canAdmin = false
    tenancy.tenant.role = role
    mount(<ClientDetailPage />, '/tenants/tenant-a/agents/agent-a')
    expect(await screen.findByRole('heading', { level: 1, name: 'SQL-01' })).toBeInTheDocument()
    expect(screen.getByText('Çevrimiçi')).toBeInTheDocument()
    expect(screen.getByRole('navigation', { name: 'Sayfa yolu' })).toHaveTextContent('Yedekleme bilgisayarları')
    expect(screen.queryByRole('button', { name: 'Bilgisayar kaydını iptal et' })).not.toBeInTheDocument()
    expect(api.getAgent).toHaveBeenCalledWith('tenant-a', 'agent-a', expect.any(AbortSignal))
  })

  it('keeps platform, versions, identifiers and lifecycle in collapsed, copyable technical details', async () => {
    mount(<ClientDetailPage />, '/tenants/tenant-a/agents/agent-a')
    await screen.findByRole('heading', { level: 1, name: 'SQL-01' })
    for (const value of ['agent-a', 'Windows', 'Windows Server', '1.2.0', '.NET 10']) expect(screen.getByText(value)).not.toBeVisible()
    fireEvent.click(screen.getByText('Teknik ayrıntılar'))
    for (const value of ['agent-a', 'Windows Server', '1.2.0', '.NET 10']) expect(screen.getByText(value)).toBeVisible()
    expect(screen.getByRole('button', { name: 'Kopyala: Bilgisayar kimliği' })).toBeInTheDocument()
  })

  it('lists reported capabilities under technical details with separate supported, unsupported and unknown states', async () => {
    const capabilities = [
      { identifier: 'mssql.backup.full', support: 'Supported', metadata: { scope: 'agent' } },
      { identifier: 'mssql.backup.checksum', support: 'Supported' },
      { identifier: 'mssql.backup.differential', support: 'Unsupported' },
      { identifier: 'vendor.custom-feature', support: 'Unknown' },
    ]
    vi.mocked(api.getAgent).mockResolvedValue({ ...agent, capabilities, capabilitiesRevision: 'c1-abc', capabilitiesReportedAt: '2026-10-06T12:00:00Z' })
    mount(<ClientDetailPage />, '/tenants/tenant-a/agents/agent-a')
    await screen.findByRole('heading', { level: 1, name: 'SQL-01' })
    const section = screen.getByRole('heading', { name: 'Bildirilen yetenekler' }).parentElement!
    expect(section).not.toBeVisible()
    fireEvent.click(screen.getByText('Teknik ayrıntılar'))
    expect(within(section).getAllByText('Destekleniyor')).toHaveLength(2)
    expect(within(section).getByText('Desteklenmiyor')).toBeInTheDocument()
    expect(within(section).getByText('Bilinmiyor')).toBeInTheDocument()
    expect(within(section).getByText('vendor.custom-feature')).toBeInTheDocument()
    expect(within(section).getByText('MSSQL Tam Yedekleme')).toBeInTheDocument()
    expect(screen.getByText('c1-abc')).toBeInTheDocument()
  })

  it('says capabilities were not reported yet instead of inventing support', async () => {
    vi.mocked(api.getAgent).mockResolvedValue({ ...agent, capabilities: [] })
    mount(<ClientDetailPage />, '/tenants/tenant-a/agents/agent-a')
    await screen.findByRole('heading', { level: 1, name: 'SQL-01' })
    fireEvent.click(screen.getByText('Teknik ayrıntılar'))
    expect(screen.getByText('Yetenekler henüz bildirilmedi.')).toBeVisible()
  })

  it('shows only this computer\'s backup jobs', async () => {
    vi.mocked(jobsApi.getBackupJobs).mockResolvedValue([
      { id: 'job-1', name: 'Muhasebe', agentId: 'agent-a', enabled: true, lastRun: null, nextOccurrenceAt: null },
      { id: 'job-2', name: 'Başka bilgisayarın işi', agentId: 'agent-z', enabled: true, lastRun: null, nextOccurrenceAt: null },
    ] as never)
    mount(<ClientDetailPage />, '/tenants/tenant-a/agents/agent-a')
    expect(await screen.findByText('Muhasebe')).toBeInTheDocument()
    expect(screen.queryByText('Başka bilgisayarın işi')).not.toBeInTheDocument()
  })

  it('requires focused confirmation before an administrator removes a computer and invalidates only related scopes', async () => {
    const { client } = mount(<ClientDetailPage />, '/tenants/tenant-a/agents/agent-a')
    const invalidate = vi.spyOn(client, 'invalidateQueries')
    fireEvent.click(await screen.findByRole('button', { name: 'Bilgisayar kaydını iptal et' }))
    const dialog = await screen.findByRole('dialog')
    await waitFor(() => expect(within(dialog).getByRole('button', { name: 'Vazgeç' })).toHaveFocus())
    expect(api.revokeAgent).not.toHaveBeenCalled()
    fireEvent.click(within(dialog).getByRole('button', { name: 'İptali onayla' }))
    await waitFor(() => expect(api.revokeAgent).toHaveBeenCalledWith('tenant-a', 'agent-a'))
    await waitFor(() => expect(invalidate).toHaveBeenCalledWith({ queryKey: ['tenant', 'tenant-a', 'agents'] }))
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['tenant', 'tenant-a', 'dashboard'] })
  })
})

describe('Adding a computer from the new-computer page', () => {
  it.each(['Viewer', 'Operator'])('%s cannot query or mutate enrollment administration', (role) => {
    tenancy.canAdmin = false
    tenancy.tenant.role = role
    mount(<AgentRegisterPage />)
    expect(screen.getByRole('heading', { level: 1, name: 'Yeni bilgisayar kaydı' })).toBeInTheDocument()
    expect(screen.getByRole('alert')).toHaveTextContent('yöneticisi')
    expect(api.getEnrollmentPolicy).not.toHaveBeenCalled()
    expect(api.getEnrollmentTokens).not.toHaveBeenCalled()
    expect(screen.queryByRole('button', { name: 'Yedekleme bilgisayarı ekle' })).not.toBeInTheDocument()
  })

  it('shows policy off and disables token creation, while admin may enable enrollment', async () => {
    vi.mocked(api.getEnrollmentPolicy).mockResolvedValue({ tenantId: 'tenant-a', enabled: false })
    mount(<AgentRegisterPage />)
    const toggle = await screen.findByRole('switch', { name: "Yeni bilgisayar kayıtlarına izin ver" })
    expect(toggle).toHaveAttribute('aria-checked', 'false')
    expect(screen.getByRole('button', { name: 'Yedekleme bilgisayarı ekle' })).toBeDisabled()
    fireEvent.click(toggle)
    await waitFor(() => expect(api.setEnrollmentPolicy).toHaveBeenCalledWith('tenant-a', true))
  })

  it('shows plaintext once without query or mutation cache persistence, and clears it on close', async () => {
    const { client } = mount(<AgentRegisterPage />)
    await screen.findByRole('switch')
    fireEvent.click(screen.getByRole('button', { name: 'Yedekleme bilgisayarı ekle' }))
    const dialog = await screen.findByRole('dialog')
    const lifetime = within(dialog).getByRole('spinbutton', { name: 'Geçerlilik süresi (dakika)' })
    fireEvent.change(lifetime, { target: { value: '60' } })
    fireEvent.click(within(dialog).getByRole('button', { name: 'İleri' }))
    fireEvent.click(within(dialog).getByRole('button', { name: "Kayıt anahtarı oluştur" }))
    expect(await screen.findByDisplayValue('secret-once-only')).toBeInTheDocument()
    expect(api.createEnrollmentToken).toHaveBeenCalledWith('tenant-a', 60, expect.any(AbortSignal))
    expect(within(dialog).getByText(/Anahtarı paylaşmayın/)).toBeInTheDocument()
    expect(JSON.stringify(client.getQueryCache().getAll().map((query) => query.state.data))).not.toContain('secret-once-only')
    expect(client.getMutationCache().getAll()).toHaveLength(0)
    expect(screen.queryByText('internal-user-id')).not.toBeInTheDocument()
    expect(screen.queryByText('token-a')).not.toBeInTheDocument()
    fireEvent.click(within(dialog).getByRole('button', { name: 'Kapat' }))
    await waitFor(() => expect(screen.queryByDisplayValue('secret-once-only')).not.toBeInTheDocument())
    fireEvent.click(screen.getByRole('button', { name: 'Yedekleme bilgisayarı ekle' }))
    expect(await screen.findByRole('spinbutton')).toHaveValue(1440)
    expect(screen.queryByDisplayValue('secret-once-only')).not.toBeInTheDocument()
  })

  it('revokes an active token only after confirmation', async () => {
    const { client } = mount(<AgentRegisterPage />)
    const invalidate = vi.spyOn(client, 'invalidateQueries')
    fireEvent.click(await screen.findByRole('button', { name: /tarihinde oluşturulan kayıt anahtarını iptal et/ }))
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
    const view = mount(<AddAgentDialog tenantId="tenant-a" onClose={vi.fn()} onCreated={onCreated} />)
    fireEvent.click(await screen.findByRole('button', { name: 'İleri' }))
    fireEvent.click(await screen.findByRole('button', { name: "Kayıt anahtarı oluştur" }))
    const signal = vi.mocked(api.createEnrollmentToken).mock.calls[0]![2]
    view.unmount()
    expect(signal.aborted).toBe(true)
    await act(async () => { resolve({ tokenId: 'token-b', token: 'late-secret', expiresAt: '2026-10-07T10:00:00Z' }) })
    expect(onCreated).not.toHaveBeenCalled()
  })

  it('clears plaintext when the tenant changes without a route unmount', async () => {
    const { client, rerender } = mount(<AgentRegisterPage />)
    await screen.findByRole('switch')
    fireEvent.click(screen.getByRole('button', { name: 'Yedekleme bilgisayarı ekle' }))
    const dialog = await screen.findByRole('dialog')
    fireEvent.click(within(dialog).getByRole('button', { name: 'İleri' }))
    fireEvent.click(within(dialog).getByRole('button', { name: "Kayıt anahtarı oluştur" }))
    await screen.findByDisplayValue('secret-once-only')
    tenancy.tenantId = 'tenant-b'
    rerender(<QueryClientProvider client={client}><MemoryRouter initialEntries={['/tenants/tenant-a/agents']}><Routes><Route path="/tenants/:tenantId/agents/:id" element={<AgentRegisterPage />} /><Route path="*" element={<AgentRegisterPage />} /></Routes></MemoryRouter></QueryClientProvider>)
    await waitFor(() => expect(screen.queryByDisplayValue('secret-once-only')).not.toBeInTheDocument())
    expect(api.getEnrollmentPolicy).toHaveBeenCalledWith('tenant-b', expect.any(AbortSignal))
  })
})
