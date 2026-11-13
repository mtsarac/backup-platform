import userEvent from '@testing-library/user-event'
import { cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react'
import { render } from '@/test/render'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { getAgents } from '@/features/agents/api'
import { getDiscoveryAgents, getDiscoveryInstances, rescanDiscoveryAgent, saveDiscoverySettings } from '@/features/discovery/api'
import { legacyAgent, makeInstance, makeStatus, supportedAgent } from '@/features/discovery/test-fixtures'
import { ApiError } from '@/lib/api/client'
import { DiscoveryPage } from './discovery-page'

const access = vi.hoisted(() => ({ tenantId: 'tenant-a', tenant: { name: 'Acme' }, canOperate: true, canAdmin: true }))
vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => access }))
vi.mock('@/features/discovery/api')
vi.mock('@/features/agents/api')

function mount(path = '/') {
  render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })}><MemoryRouter initialEntries={[path]}><DiscoveryPage /></MemoryRouter></QueryClientProvider>)
}
beforeEach(() => {
  vi.clearAllMocks()
  Object.assign(access, { canOperate: true, canAdmin: true })
  vi.mocked(getAgents).mockResolvedValue([supportedAgent, legacyAgent] as never)
  vi.mocked(getDiscoveryAgents).mockResolvedValue({ items: [makeStatus()] })
  vi.mocked(getDiscoveryInstances).mockResolvedValue({ items: [makeInstance(), makeInstance({ id: 'inst-2', displayName: 'Other server', access: 'CredentialsRequired' })] })
  vi.mocked(saveDiscoverySettings).mockResolvedValue(makeStatus())
})

describe('discovery page', () => {
  it('links each server to its databases and keeps scan internals collapsed and copyable', async () => {
    mount()
    expect(await screen.findByRole('link', { name: 'SQL Server (default)' })).toHaveAttribute('href', '/tenants/tenant-a/discovery/instances/inst-1')
    expect(screen.getByText('Kimlik bilgisi gerekli')).toBeInTheDocument()
    const computer = screen.getByRole('region', { name: 'SQL Agent' })
    expect(within(computer).getByText('Liste güncel')).toBeInTheDocument()
    expect(screen.queryByText(/Docker yok demek değildir/)).not.toBeInTheDocument()
    await userEvent.click(within(computer).getByRole('button', { name: 'Teknik ayrıntılar' }))
    const details = await screen.findByRole('dialog')
    expect(within(details).getByText(/Docker yok demek değildir/)).toBeInTheDocument()
    expect(within(details).getByRole('button', { name: 'Kopyala: Bilgisayar kimliği' })).toBeInTheDocument()
  })
  it('links the computer from its scan card and from every server row', async () => {
    mount()
    const card = await screen.findByRole('region', { name: 'SQL Agent' })
    expect(within(card).getByRole('link', { name: 'SQL Agent' })).toHaveAttribute('href', '/tenants/tenant-a/agents/agent-a')
    const row = screen.getByRole('link', { name: 'SQL Server (default)' }).closest('tr')!
    expect(within(row).getByRole('link', { name: 'SQL Agent' })).toHaveAttribute('href', '/tenants/tenant-a/agents/agent-a')
  })
  it('opens already filtered from the URL and ignores values it does not know', async () => {
    mount('/?agentId=agent-a&engine=mssql&includeMissing=1')
    await screen.findByRole('link', { name: 'SQL Server (default)' })
    expect(getDiscoveryInstances).toHaveBeenLastCalledWith('tenant-a', { agentId: 'agent-a', engine: 'mssql', includeMissing: true }, expect.anything())
    expect(screen.getByLabelText('Veritabanı türü')).toHaveValue('mssql')
    cleanup()
    mount('/?agentId=gone&engine=oracle')
    await screen.findByRole('link', { name: 'SQL Server (default)' })
    expect(getDiscoveryInstances).toHaveBeenLastCalledWith('tenant-a', {}, expect.anything())
  })
  it('shows the scan interval once for administrators and read-only for others', async () => {
    mount()
    fireEvent.click((await screen.findAllByText('Teknik ayrıntılar'))[0])
    expect(screen.getAllByText('Tarama aralığı (dakika)')).toHaveLength(1)
    expect(screen.getByLabelText('Tarama aralığı (dakika)')).toHaveValue(360)
    cleanup()
    Object.assign(access, { canOperate: false, canAdmin: false })
    mount()
    fireEvent.click((await screen.findAllByText('Teknik ayrıntılar'))[0])
    expect(screen.getByText('Tarama aralığı (dakika)')).toBeInTheDocument()
  })
  it('says an offline computer scans when it reconnects', async () => {
    vi.mocked(getAgents).mockResolvedValue([{ ...supportedAgent, connectivity: 'offline' }] as never)
    mount()
    expect(await screen.findByText(/şu anda çevrimdışı; tarama bilgisayar yeniden bağlandığında çalışır/)).toBeInTheDocument()
  })
  it('has one rescan per computer and explains why an unsupported computer cannot be scanned', async () => {
    mount()
    expect(await screen.findByRole('button', { name: 'Old Agent yeniden tara' })).toBeDisabled()
    expect(screen.getByText(/Programı güncelledikten sonra/)).toBeInTheDocument()
    expect(screen.getAllByRole('button', { name: /yeniden tara/ })).toHaveLength(2)
    expect(screen.getByRole('button', { name: 'SQL Agent yeniden tara' })).toBeEnabled()
  })
  it.each(['Requested', 'Running'] as const)('does not queue another scan while %s', async scanState => {
    vi.mocked(getDiscoveryAgents).mockResolvedValue({ items: [makeStatus({ scanState })] })
    mount()
    expect(await screen.findByRole('button', { name: 'SQL Agent yeniden tara' })).toBeDisabled()
    expect(screen.getByRole('status', { name: 'Tarama durumu' })).toHaveTextContent(scanState === 'Requested' ? 'Tarama istendi' : 'Taranıyor')
  })
  it('explains a failed scan without claiming backups were damaged or exposing a raw code', async () => {
    vi.mocked(getDiscoveryAgents).mockResolvedValue({ items: [makeStatus({ lastOutcome: 'Failed', lastFailureCode: 'new_scan_code' })] })
    mount()
    expect(await screen.findByText('Tarama başarısız')).toBeInTheDocument()
    const alert = screen.getByRole('alert')
    expect(within(alert).getByText('Verileriniz için:')).toBeInTheDocument()
    expect(within(alert).getByText('Ne yapmalı:')).toBeInTheDocument()
    expect(within(alert).queryByText('new_scan_code')).not.toBeInTheDocument()
    expect(screen.queryByText('new_scan_code')).not.toBeInTheDocument()
    await userEvent.click(screen.getAllByRole('button', { name: 'Teknik ayrıntılar' })[0])
    expect(within(await screen.findByRole('dialog')).getByText('new_scan_code')).toBeInTheDocument()
  })
  it('warns that a partial scan may be incomplete', async () => {
    vi.mocked(getDiscoveryAgents).mockResolvedValue({ items: [makeStatus({ lastOutcome: 'Partial' })] })
    mount()
    expect(await screen.findByRole('status', { name: 'Tarama durumu' })).toHaveTextContent('Kısmen tarandı')
    expect(screen.getByText(/liste eksik olabilir/)).toBeInTheDocument()
  })
  it('does not invent a scan result before a computer reports one', async () => {
    vi.mocked(getDiscoveryAgents).mockResolvedValue({ items: [] })
    mount()
    expect(await screen.findByText('Henüz taranmadı')).toBeInTheDocument()
    expect(screen.queryByText('Liste güncel')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'SQL Agent yeniden tara' })).toBeEnabled()
  })
  it('shows a load failure rather than an invented empty computer list', async () => {
    vi.mocked(getAgents).mockRejectedValue(new ApiError(500))
    mount()
    expect(await screen.findByRole('alert')).toBeInTheDocument()
    expect(screen.queryByText(/Henüz yedekleme bilgisayarı yok/)).not.toBeInTheDocument()
  })
  it('shows a friendly text for a throttled rescan', async () => {
    vi.mocked(rescanDiscoveryAgent).mockRejectedValue(new ApiError(429, { error: 'rescan_too_soon' }))
    mount()
    fireEvent.click(await screen.findByRole('button', { name: 'SQL Agent yeniden tara' }))
    expect(await screen.findByText(/Az önce tarama istendi/)).toBeInTheDocument()
    expect(rescanDiscoveryAgent).toHaveBeenCalledWith('tenant-a', 'agent-a')
    expect(screen.getByText('Verileriniz için:')).toBeInTheDocument()
    expect(screen.getByText('Ne yapmalı:')).toBeInTheDocument()
  })
  it('hides every mutating control from a Viewer', async () => {
    Object.assign(access, { canOperate: false, canAdmin: false })
    mount()
    await screen.findByRole('link', { name: 'SQL Server (default)' })
    expect(screen.queryByRole('button', { name: /yeniden tara/ })).not.toBeInTheDocument()
    expect(screen.queryByLabelText('Otomatik tarama açık')).not.toBeInTheDocument()
  })
  it('lets administrators change scan settings only under technical details', async () => {
    mount()
    await screen.findByRole('region', { name: 'SQL Agent' })
    expect(screen.queryByLabelText('Tarama ayarları')).not.toBeInTheDocument()
    await userEvent.click(screen.getAllByRole('button', { name: 'Teknik ayrıntılar' })[0])
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByLabelText('Tarama ayarları')).toBeInTheDocument()
    fireEvent.change(screen.getByLabelText('Tarama aralığı (dakika)'), { target: { value: '30' } })
    fireEvent.click(screen.getByRole('button', { name: 'Kaydet' }))
    await waitFor(() => expect(saveDiscoverySettings).toHaveBeenCalledWith('tenant-a', 'agent-a', { enabled: true, scanIntervalMinutes: 30, includeDocker: true }))
  })
  it('guides a tenant with no computer to add one', async () => {
    vi.mocked(getAgents).mockResolvedValue([])
    mount()
    expect(await screen.findByRole('link', { name: 'Yedekleme bilgisayarı ekle' })).toHaveAttribute('href', '/tenants/tenant-a/agents')
    expect(screen.getByText(/Henüz yedekleme bilgisayarı yok/)).toBeInTheDocument()
    expect(screen.queryByText(/program veritabanı bulmayı desteklemiyor/)).not.toBeInTheDocument()
  })
  it('guides tenants with an unsupported program to update or enter databases manually', async () => {
    vi.mocked(getAgents).mockResolvedValue([legacyAgent] as never)
    mount()
    expect(await screen.findByText(/programı güncelleyin veya veritabanını/)).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Yedekleme bilgisayarlarına git' })).toHaveAttribute('href', '/tenants/tenant-a/agents')
    expect(screen.queryByText(/Henüz yedekleme bilgisayarı yok/)).not.toBeInTheDocument()
  })
  it('guides users with no results to the existing rescan, without adding another scan action', async () => {
    vi.mocked(getDiscoveryInstances).mockResolvedValue({ items: [] })
    mount()
    expect(await screen.findByText(/henüz veritabanı bulunamadı/)).toBeInTheDocument()
    expect(screen.getAllByRole('button', { name: 'SQL Agent yeniden tara' })).toHaveLength(1)
  })
  it('tells Viewers who can add the missing computer', async () => {
    Object.assign(access, { canOperate: false, canAdmin: false })
    vi.mocked(getAgents).mockResolvedValue([])
    mount()
    expect(await screen.findByText('Yöneticinize danışın')).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'Yedekleme bilgisayarı ekle' })).not.toBeInTheDocument()
  })
  it('renders a permission state on 403', async () => {
    vi.mocked(getDiscoveryInstances).mockRejectedValue(new ApiError(403))
    mount()
    expect(await screen.findByText(/görüntüleme yetkiniz yok/)).toBeInTheDocument()
  })
  it('renders a three-part load error without backend text', async () => {
    vi.mocked(getDiscoveryInstances).mockRejectedValue(new ApiError(500, { error: 'secret stack' }))
    mount()
    expect(await screen.findByText(/Sunucu isteği tamamlayamadı/)).toBeInTheDocument()
    expect(screen.getByText('Verileriniz için:')).toBeInTheDocument()
    expect(screen.getByText('Ne yapmalı:')).toBeInTheDocument()
    expect(screen.queryByText(/secret stack/)).not.toBeInTheDocument()
  })
  it('passes filters to the API', async () => {
    mount()
    await screen.findByRole('link', { name: 'SQL Server (default)' })
    fireEvent.change(screen.getByLabelText('Veritabanı türü'), { target: { value: 'mssql' } })
    fireEvent.click(screen.getByLabelText('Bulunamayanları da göster'))
    fireEvent.change(screen.getByLabelText('Bilgisayar'), { target: { value: 'agent-a' } })
    await waitFor(() => expect(getDiscoveryInstances).toHaveBeenLastCalledWith('tenant-a', { agentId: 'agent-a', engine: 'mssql', includeMissing: true }, expect.anything()))
  })
})
