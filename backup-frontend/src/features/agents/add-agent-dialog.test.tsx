/// <reference lib="es2024.promise" />
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { act, fireEvent, screen, within } from '@testing-library/react'
import { render } from '@/test/render'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { AddAgentDialog } from './add-agent-dialog'
import { ClientsPage } from '@/pages/clients/clients-page'
import { enrollmentCommands, httpsServerOrigin, suggestedServerOrigin } from './enrollment-commands'
import type { AgentDetail, CreatedEnrollmentToken, EnrollmentTokenStatus } from './types'
import { ApiError } from '@/lib/api/client'
import * as api from './api'

const tenancy = vi.hoisted(() => ({ tenantId: 'tenant-a', canAdmin: true, canOperate: true }))
vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => tenancy }))
vi.mock('./api', () => ({ getAgentDistribution: vi.fn(), getAgents: vi.fn(), getAgent: vi.fn(), createEnrollmentToken: vi.fn(), getEnrollmentTokenStatus: vi.fn(), revokeEnrollmentToken: vi.fn() }))
const detail: AgentDetail = { agentId: 'agent-a', hostName: 'Backup host', platform: 'Windows', osDescription: 'Windows Server', agentVersion: '1.2', protocolVersion: '1', runtimeVersion: '.NET', status: 'Active', connectivity: 'offline', operationalStatus: 'Offline', lastSeenAt: null, lastHeartbeatAt: null, lastConnectedAt: null, capabilityRevision: null, capabilities: [], enrolledAt: null }
let expiresAt: string
beforeEach(() => {
  vi.resetAllMocks(); vi.useFakeTimers()
  Object.assign(tenancy, { tenantId: 'tenant-a', canAdmin: true, canOperate: true })
  expiresAt = new Date(Date.now() + 60_000).toISOString()
  vi.mocked(api.getAgentDistribution).mockResolvedValue({ serverUrl: 'https://backend.example:7443', packages: { 'win-x64': true, 'linux-x64': true } })
  vi.mocked(api.getAgents).mockResolvedValue([])
  vi.mocked(api.createEnrollmentToken).mockResolvedValue({ tokenId: 'token-a', token: 'transient-secret', expiresAt })
  vi.mocked(api.getEnrollmentTokenStatus).mockResolvedValue({ tokenId: 'token-a', expiresAt, state: 'Active', agentId: null })
  vi.mocked(api.getAgent).mockResolvedValue(detail)
  vi.mocked(api.revokeEnrollmentToken).mockResolvedValue(undefined)
})
afterEach(() => { vi.useRealTimers(); vi.restoreAllMocks() })
function mount() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const close = vi.fn()
  const view = render(<QueryClientProvider client={client}><AddAgentDialog tenantId="tenant-a" onClose={close} /></QueryClientProvider>)
  return { client, close, ...view }
}
async function issue() {
  fireEvent.click(screen.getByRole('tab', { name: 'Otomatik kurulum' }))
  const name = screen.getByRole('textbox', { name: 'Ajan adı' }) as HTMLInputElement
  if (!name.value) fireEvent.change(name, { target: { value: 'Backup agent' } })
  fireEvent.change(screen.getByRole('textbox', { name: "Sunucu HTTPS adresi" }), { target: { value: 'https://backend.example:7443' } })
  await act(async () => { fireEvent.click(screen.getByRole('button', { name: "Kayıt anahtarı oluştur" })) })
}

describe('Enrollment commands', () => {
  it('uses HTTPS one-line installers and shell-escapes names and keys for both platforms', () => {
    expect(enrollmentCommands('Windows', 'https://backend.example', "token'$()", "O'Brien")).toContain("-Token 'token''$()' -AgentName 'O''Brien' -NonInteractive -Language tr")
    expect(enrollmentCommands('Linux', 'https://backend.example', 'token', "O'Brien")).toContain("--name 'O'\\''Brien' --non-interactive --language tr")
    expect(enrollmentCommands('Linux', 'https://backend.example', 'token', "O'Brien")).toContain("curl --proto '=https'")
    expect(enrollmentCommands('Linux', 'https://backend.example', 'token', '')).toBe('')
    for (const platform of ['Windows', 'Linux'] as const) {
      const guided = enrollmentCommands(platform, 'https://backend.example', 'never-in-command', '', 'interactive', 'en')
      expect(guided).not.toContain('never-in-command')
      expect(guided).toContain(platform === 'Windows' ? '-Interactive -Language en' : '--interactive --language en')
    }
  })
  it('derives only secure origins and never generates an insecure command', () => {
    expect(suggestedServerOrigin('/api', 'https://frontend.example')).toBe('https://frontend.example')
    expect(suggestedServerOrigin('https://backend.example/api', 'http://localhost:5173')).toBe('https://backend.example')
    expect(suggestedServerOrigin('/api', 'http://localhost:5173')).toBe('')
    for (const value of ['http://backend.example', 'https://user:pass@backend.example', 'https://backend.example/api', 'https://backend.example?token=x']) expect(httpsServerOrigin(value)).toBe('')
    expect(enrollmentCommands('Linux', 'http://backend.example', 'secret', '')).toBe('')
  })
})

describe('Add Agent lifecycle', () => {
  it('waits, clears consumed plaintext, observes connecting, completes only on heartbeat online and stops', async () => {
    const { client } = mount()
    await issue()
    expect(screen.getByRole('status')).toHaveTextContent('Bekleniyor')
    expect(screen.getByRole('textbox', { name: "Tek kullanımlık kurulum komutu" })).toHaveValue(enrollmentCommands('Windows', 'https://backend.example:7443', 'transient-secret', 'Backup agent'))
    vi.mocked(api.getEnrollmentTokenStatus).mockResolvedValue({ tokenId: 'token-a', expiresAt, state: 'Consumed', agentId: 'agent-a' })
    await act(async () => { await vi.advanceTimersByTimeAsync(5_000) })
    expect(screen.getByRole('status')).toHaveTextContent('Kayıt tamamlandı')
    expect(screen.queryByRole('textbox', { name: "Tek kullanımlık kurulum komutu" })).not.toBeInTheDocument()
    await act(async () => { await vi.advanceTimersByTimeAsync(5_000) })
    expect(screen.getByRole('status')).toHaveTextContent('Bağlanıyor')
    expect(screen.getByText('agent-a')).toBeInTheDocument()
    expect(screen.getByText('Windows Server')).toBeInTheDocument()
    vi.mocked(api.getAgent).mockResolvedValue({ ...detail, connectivity: 'online' })
    await act(async () => { await vi.advanceTimersByTimeAsync(5_000) })
    expect(screen.getByRole('status')).toHaveTextContent('Çevrimiçi')
    const count = vi.mocked(api.getAgent).mock.calls.length
    await act(async () => { await vi.advanceTimersByTimeAsync(60_000) })
    expect(api.getAgent).toHaveBeenCalledTimes(count)
    expect(api.getAgent).toHaveBeenCalledWith('tenant-a', 'agent-a', expect.any(AbortSignal))
    expect(JSON.stringify(client.getQueryCache().getAll().map(query => query.state.data))).not.toContain('transient-secret')
    expect(client.getMutationCache().getAll()).toHaveLength(0)
  })
  it('keeps plaintext out of browser storage and caches, and clears on close', async () => {
    const storage = vi.spyOn(Storage.prototype, 'setItem')
    const { client, close } = mount()
    await issue()
    expect(storage).not.toHaveBeenCalled()
    expect(JSON.stringify(client.getQueryCache().getAll().map(q => q.state.data))).not.toContain('transient-secret')
    expect(client.getMutationCache().getAll()).toHaveLength(0)
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Kapat' })) })
    expect(close).toHaveBeenCalledOnce()
    expect(screen.queryByRole('textbox', { name: "Tek kullanımlık kurulum komutu" })).not.toBeInTheDocument()
  })
  it('clears plaintext while hidden at deadline, then confirms authoritative expiry and stops', async () => {
    mount(); await issue()
    const visibility = vi.spyOn(document, 'visibilityState', 'get').mockReturnValue('hidden')
    fireEvent(document, new Event('visibilitychange'))
    const count = vi.mocked(api.getEnrollmentTokenStatus).mock.calls.length
    await act(async () => { await vi.advanceTimersByTimeAsync(60_000) })
    expect(screen.getByRole('status')).toHaveTextContent('kayıt durumu doğrulanıyor')
    expect(screen.queryByRole('textbox', { name: "Tek kullanımlık kurulum komutu" })).not.toBeInTheDocument()
    expect(api.getEnrollmentTokenStatus).toHaveBeenCalledTimes(count)
    visibility.mockReturnValue('visible')
    vi.mocked(api.getEnrollmentTokenStatus).mockResolvedValue({ tokenId: 'token-a', expiresAt, state: 'Expired', agentId: null })
    await act(async () => { fireEvent(document, new Event('visibilitychange')) })
    expect(screen.getByRole('status')).toHaveTextContent("Kayıt anahtarının süresi doldu")
    expect(api.getEnrollmentTokenStatus).toHaveBeenCalledTimes(count + 1)
    await act(async () => { await vi.advanceTimersByTimeAsync(60_000) })
    expect(api.getEnrollmentTokenStatus).toHaveBeenCalledTimes(count + 1)
  })
  it('pauses while hidden and resumes without concurrent polling', async () => {
    mount(); await issue()
    const visibility = vi.spyOn(document, 'visibilityState', 'get').mockReturnValue('hidden')
    fireEvent(document, new Event('visibilitychange'))
    await act(async () => { await vi.advanceTimersByTimeAsync(15_000) })
    expect(api.getEnrollmentTokenStatus).toHaveBeenCalledTimes(1)
    visibility.mockReturnValue('visible')
    await act(async () => { fireEvent(document, new Event('visibilitychange')) })
    expect(api.getEnrollmentTokenStatus).toHaveBeenCalledTimes(2)
  })
  it.each(['Revoked', 'Consumed', 'Expired'] as const)('handles %s without leaking or rereading plaintext', async state => {
    vi.mocked(api.getEnrollmentTokenStatus).mockResolvedValue({ tokenId: 'token-a', expiresAt, state, agentId: null })
    mount(); await issue()
    expect(screen.queryByRole('textbox', { name: "Tek kullanımlık kurulum komutu" })).not.toBeInTheDocument()
    await act(async () => { await vi.advanceTimersByTimeAsync(15_000) })
    expect(api.getEnrollmentTokenStatus).toHaveBeenCalledTimes(1)
    expect(screen.getByRole('button', { name: "Yeni kayıt anahtarı oluştur" })).toBeEnabled()
  })
  it('revokes the old active token before creating its replacement', async () => {
    mount(); await issue()
    vi.mocked(api.createEnrollmentToken).mockResolvedValue({ tokenId: 'token-b', token: 'replacement-secret', expiresAt })
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: "Yeni kayıt anahtarı oluştur" })) })
    expect(api.revokeEnrollmentToken).toHaveBeenCalledWith('tenant-a', 'token-a', expect.any(AbortSignal))
    expect(vi.mocked(api.revokeEnrollmentToken).mock.invocationCallOrder[0]).toBeLessThan(vi.mocked(api.createEnrollmentToken).mock.invocationCallOrder[1]!)
    expect(screen.getByRole('textbox', { name: "Tek kullanımlık kurulum komutu" })).toHaveValue(enrollmentCommands('Windows', 'https://backend.example:7443', 'replacement-secret', 'Backup agent'))
  })
  it('backs off on safe errors, stops after three failures and offers retry', async () => {
    vi.mocked(api.getEnrollmentTokenStatus).mockRejectedValue(new ApiError(503))
    mount(); await issue()
    expect(screen.getByRole('alert')).toHaveTextContent('Sunucu isteği tamamlayamadı')
    await act(async () => { await vi.advanceTimersByTimeAsync(30_000) })
    expect(api.getEnrollmentTokenStatus).toHaveBeenCalledTimes(3)
    expect(screen.getByRole('button', { name: 'Takibi tekrar dene' })).toBeEnabled()
    vi.mocked(api.getEnrollmentTokenStatus).mockResolvedValue({ tokenId: 'token-a', expiresAt, state: 'Active', agentId: null })
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Takibi tekrar dene' })) })
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })
  it('bounds connecting polling and can resume after token expiry without losing enrollment', async () => {
    vi.mocked(api.getEnrollmentTokenStatus).mockResolvedValue({ tokenId: 'token-a', expiresAt, state: 'Consumed', agentId: 'agent-a' })
    mount(); await issue()
    await act(async () => { await vi.advanceTimersByTimeAsync(600_000) })
    expect(screen.getByRole('button', { name: 'Takibi tekrar dene' })).toBeEnabled()
    const count = vi.mocked(api.getAgent).mock.calls.length
    await act(async () => { await vi.advanceTimersByTimeAsync(60_000) })
    expect(api.getAgent).toHaveBeenCalledTimes(count)
    vi.mocked(api.getAgent).mockResolvedValue({ ...detail, connectivity: 'online' })
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Takibi tekrar dene' })) })
    expect(screen.getByRole('status')).toHaveTextContent('Çevrimiçi')
  })
  it('does not abandon another active token if revocation fails', async () => {
    mount(); await issue()
    vi.mocked(api.revokeEnrollmentToken).mockRejectedValue(new ApiError(503))
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: "Yeni kayıt anahtarı oluştur" })) })
    expect(api.createEnrollmentToken).toHaveBeenCalledTimes(1)
    expect(screen.getByRole('alert')).toHaveTextContent('Sunucu isteği tamamlayamadı')
    expect(screen.queryByRole('textbox', { name: "Tek kullanımlık kurulum komutu" })).not.toBeInTheDocument()
  })
  it('shows disabled enrollment safely and permits retry without a token', async () => {
    vi.mocked(api.createEnrollmentToken).mockRejectedValue(new ApiError(403, { code: 'enrollment_disabled' }))
    mount(); await issue()
    expect(screen.getByRole('alert')).toHaveTextContent('kayıt iznini açın')
    expect(screen.getByRole('button', { name: "Kayıt anahtarı oluştur" })).toBeEnabled()
  })
  it('renders the Linux command using the selected platform and optional name', async () => {
    mount()
    fireEvent.change(screen.getByRole('combobox', { name: 'Platform' }), { target: { value: 'Linux' } })
    fireEvent.change(screen.getByRole('textbox', { name: 'Ajan adı' }), { target: { value: 'Linux host' } })
    await issue()
    expect(screen.getByRole('textbox', { name: "Tek kullanımlık kurulum komutu" })).toHaveValue(enrollmentCommands('Linux', 'https://backend.example:7443', 'transient-secret', 'Linux host'))
  })
  it('discovers consumption before expiry while hidden, and tracks the Agent after deadline', async () => {
    mount(); await issue()
    const visibility = vi.spyOn(document, 'visibilityState', 'get').mockReturnValue('hidden')
    fireEvent(document, new Event('visibilitychange'))
    vi.mocked(api.getEnrollmentTokenStatus).mockResolvedValue({ tokenId: 'token-a', expiresAt, state: 'Consumed', agentId: 'agent-a' })
    await act(async () => { await vi.advanceTimersByTimeAsync(65_000) })
    expect(screen.queryByRole('textbox', { name: "Tek kullanımlık kurulum komutu" })).not.toBeInTheDocument()
    visibility.mockReturnValue('visible')
    await act(async () => { fireEvent(document, new Event('visibilitychange')) })
    expect(screen.getByRole('status')).toHaveTextContent('Kayıt tamamlandı')
    vi.mocked(api.getAgent).mockResolvedValue({ ...detail, connectivity: 'online' })
    await act(async () => { await vi.advanceTimersByTimeAsync(5_000) })
    expect(screen.getByRole('status')).toHaveTextContent('Çevrimiçi')
  })
  it('resumes polling before an aborted hidden request settles without accepting its late result', async () => {
    const { promise, resolve } = Promise.withResolvers<EnrollmentTokenStatus>()
    vi.mocked(api.getEnrollmentTokenStatus).mockReturnValueOnce(promise)
    mount(); await issue()
    const signal = vi.mocked(api.getEnrollmentTokenStatus).mock.calls[0]![2]!
    const visibility = vi.spyOn(document, 'visibilityState', 'get').mockReturnValue('hidden')
    fireEvent(document, new Event('visibilitychange'))
    expect(signal.aborted).toBe(true)
    visibility.mockReturnValue('visible')
    await act(async () => { fireEvent(document, new Event('visibilitychange')) })
    expect(api.getEnrollmentTokenStatus).toHaveBeenCalledTimes(2)
    await act(async () => { resolve({ tokenId: 'token-a', expiresAt, state: 'Consumed', agentId: 'wrong-agent' }) })
    await act(async () => { await vi.advanceTimersByTimeAsync(5_000) })
    expect(api.getEnrollmentTokenStatus).toHaveBeenCalledTimes(3)
    expect(api.getAgent).not.toHaveBeenCalled()
  })
  it('aborts status polling and ignores late enrollment after unmount', async () => {
    const { promise, resolve } = Promise.withResolvers<EnrollmentTokenStatus>()
    vi.mocked(api.getEnrollmentTokenStatus).mockReturnValue(promise)
    const view = mount(); await issue()
    const signal = vi.mocked(api.getEnrollmentTokenStatus).mock.calls[0]![2]!
    view.unmount()
    expect(signal.aborted).toBe(true)
    await act(async () => { resolve({ tokenId: 'token-a', expiresAt, state: 'Consumed', agentId: 'agent-a' }) })
    expect(api.getAgent).not.toHaveBeenCalled()
  })
  it('aborts issuance and ignores late plaintext on unmount', async () => {
    const { promise, resolve } = Promise.withResolvers<CreatedEnrollmentToken>()
    vi.mocked(api.createEnrollmentToken).mockReturnValue(promise)
    const view = mount(); await issue()
    const signal = vi.mocked(api.createEnrollmentToken).mock.calls[0]![2]
    view.unmount()
    expect(signal.aborted).toBe(true)
    await act(async () => { resolve({ tokenId: 'late', token: 'late-secret', expiresAt }) })
    expect(screen.queryByDisplayValue(/late-secret/)).not.toBeInTheDocument()
  })
  it.each(['Viewer', 'Operator', 'PlatformAdmin'])('%s without tenant Admin cannot add Agent', async () => {
    tenancy.canAdmin = false
    render(<QueryClientProvider client={new QueryClient()}><MemoryRouter><ClientsPage /></MemoryRouter></QueryClientProvider>)
    await act(async () => {})
    expect(screen.queryByRole('button', { name: "Ajan ekle" })).not.toBeInTheDocument()
    expect(api.createEnrollmentToken).not.toHaveBeenCalled()
  })
  it('tenant Admin sees Add Agent, and tenant changes unmount plaintext', async () => {
    const client = new QueryClient()
    const tree = <QueryClientProvider client={client}><MemoryRouter><ClientsPage /></MemoryRouter></QueryClientProvider>
    const view = render(tree)
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: "Ajan ekle" })) })
    await issue()
    expect(within(screen.getByRole('dialog')).getByRole('textbox', { name: "Tek kullanımlık kurulum komutu" })).toBeInTheDocument()
    tenancy.tenantId = 'tenant-b'
    view.rerender(<QueryClientProvider client={client}><MemoryRouter><ClientsPage /></MemoryRouter></QueryClientProvider>)
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })
})
