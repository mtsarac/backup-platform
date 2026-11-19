import userEvent from '@testing-library/user-event'
import { cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react'
import { render } from '@/test/render'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { getAgents } from '@/features/agents/api'
import { getDiscoveryAgents, getDiscoveryInstances, rescanDiscoveryAgent } from '@/features/discovery/api'
import { legacyAgent, makeInstance, makeStatus, supportedAgent } from '@/features/discovery/test-fixtures'
import { ApiError } from '@/lib/api/client'
import { DiscoveryPage } from './discovery-page'

const access = vi.hoisted(() => ({ tenantId: 'tenant-a', tenant: { name: 'Acme' } }))
vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => access }))
vi.mock('@/features/discovery/api')
vi.mock('@/features/agents/api')

function mount(path: string | { pathname: string; state: unknown } = '/') {
  render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })}><MemoryRouter initialEntries={[path]}><DiscoveryPage /></MemoryRouter></QueryClientProvider>)
}
beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(getAgents).mockResolvedValue([supportedAgent, legacyAgent] as never)
  vi.mocked(getDiscoveryAgents).mockResolvedValue({ items: [makeStatus()] })
  vi.mocked(getDiscoveryInstances).mockResolvedValue({ items: [makeInstance(), makeInstance({ id: 'inst-2', displayName: 'Other server', access: 'CredentialsRequired' })] })
})

describe('discovery page', () => {
  it('links each server to its databases and keeps scan internals collapsed and copyable', async () => {
    mount()
    expect(await screen.findByRole('link', { name: 'SQL Server (default)' })).toHaveAttribute('href', '/tenants/tenant-a/discovery/instances/inst-1')
    expect(screen.getByText('Veritabanı bulma filtreleri', { selector: 'summary' }).parentElement).not.toHaveAttribute('open')
    expect(screen.getByText('Kimlik bilgisi gerekli')).toBeInTheDocument()
    const computer = screen.getByRole('region', { name: 'SQL Agent' })
    expect(within(computer).getByText('Liste güncel')).toBeInTheDocument()
    expect(screen.queryByText(/Docker yok demek değildir/)).not.toBeInTheDocument()
    await userEvent.click(within(computer).getByRole('button', { name: 'Teknik ayrıntılar' }))
    const details = within(computer).getByRole('region', { name: 'Teknik ayrıntılar' })
    expect(within(details).getByText(/Docker yok demek değildir/)).toBeInTheDocument()
    expect(within(details).getByRole('button', { name: 'Kopyala' })).toBeInTheDocument()
  })
  it('links the computer from its scan card and from every server row', async () => {
    mount()
    const card = await screen.findByRole('region', { name: 'SQL Agent' })
    expect(within(card).getByRole('link', { name: 'SQL Agent' })).toHaveAttribute('href', '/tenants/tenant-a/agents/agent-a')
    const row = screen.getByRole('link', { name: 'SQL Server (default)' }).closest('tr')!
    expect(within(row).getByRole('link', { name: 'SQL Agent' })).toHaveAttribute('href', '/tenants/tenant-a/agents/agent-a')
  })
  it('does not list servers that belong to a computer that is no longer in the list', async () => {
    vi.mocked(getDiscoveryInstances).mockResolvedValue({ items: [makeInstance(), makeInstance({ id: 'inst-old', agentId: 'agent-revoked', displayName: 'Old computer server' })] })
    mount()
    expect(await screen.findByRole('link', { name: 'SQL Server (default)' })).toBeInTheDocument()
    expect(screen.queryByText('Old computer server')).not.toBeInTheDocument()
  })
  it('opens already filtered from the URL and ignores values it does not know', async () => {
    mount('/?agentId=agent-a&engine=mssql&includeMissing=1')
    await screen.findByRole('link', { name: 'SQL Server (default)' })
    expect(getDiscoveryInstances).toHaveBeenLastCalledWith('tenant-a', { agentId: 'agent-a', engine: 'mssql', includeMissing: true }, expect.anything())
    expect(screen.getByText('Veritabanı bulma filtreleri', { selector: 'summary' }).parentElement).toHaveAttribute('open')
    expect(screen.getByLabelText('Veritabanı türü')).toHaveValue('mssql')
    cleanup()
    mount('/?agentId=gone&engine=oracle')
    await screen.findByRole('link', { name: 'SQL Server (default)' })
    expect(getDiscoveryInstances).toHaveBeenLastCalledWith('tenant-a', {}, expect.anything())
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
    expect(within(alert).queryByText('new_scan_code')).not.toBeInTheDocument()
    expect(screen.queryByText('new_scan_code')).not.toBeInTheDocument()
    await userEvent.click(screen.getAllByRole('button', { name: 'Teknik ayrıntılar' })[0])
    expect(within(screen.getByRole('region', { name: 'Teknik ayrıntılar' })).getByText('new_scan_code')).toBeInTheDocument()
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
  describe('a rescan asked twice in a row', () => {
    const rescan = async () => {
      vi.mocked(rescanDiscoveryAgent).mockRejectedValue(new ApiError(429, { error: 'rescan_too_soon' }))
      mount()
      fireEvent.click(await screen.findByRole('button', { name: 'SQL Agent yeniden tara' }))
      await waitFor(() => expect(rescanDiscoveryAgent).toHaveBeenCalledWith('tenant-a', 'agent-a'))
      return (await screen.findByText(/Az önce tarama istendi/)).closest('[role]') as HTMLElement
    }
    it('is an info notice, not a red error, and has no technical details of its own', async () => {
      const notice = await rescan()
      expect(notice).toHaveAttribute('role', 'status')
      expect(screen.queryByRole('alert')).not.toBeInTheDocument()
      expect(within(notice).queryByRole('button', { name: 'Teknik ayrıntılar' })).not.toBeInTheDocument()
    })
    it('goes away by itself after a few seconds', async () => {
      await rescan()
      await waitFor(() => expect(screen.queryByText(/Az önce tarama istendi/)).not.toBeInTheDocument(), { timeout: 8000 })
    }, 15000)
  })
  it('still offers to clear the filters when a chosen filter matches nothing', async () => {
    vi.mocked(getDiscoveryInstances).mockResolvedValue({ items: [] })
    mount('/?engine=mssql')
    expect(await screen.findByText(/Seçilen filtrelere uyan/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Filtreleri temizle' })).toBeInTheDocument()
  })
  it('names a missing TLS in the status of the server row instead of a bare "Hata"', async () => {
    vi.mocked(getDiscoveryInstances).mockResolvedValue({ items: [makeInstance({ access: 'Error', accessReasonCode: 'server_tls_unavailable' })] })
    mount()
    expect(await screen.findByText('Şifreli bağlantı yok')).toBeInTheDocument()
    expect(screen.queryByText('Hata')).not.toBeInTheDocument()
  })
  it('offers nobody scan settings: automatic scans and Docker stay on, the interval is shown read-only', async () => {
    mount()
    await screen.findByRole('region', { name: 'SQL Agent' })
    await userEvent.click(screen.getAllByRole('button', { name: 'Teknik ayrıntılar' })[0])
    const details = screen.getByRole('region', { name: 'Teknik ayrıntılar' })
    expect(within(details).queryByLabelText('Tarama ayarları')).not.toBeInTheDocument()
    expect(screen.queryByLabelText('Otomatik tarama açık')).not.toBeInTheDocument()
    expect(screen.queryByLabelText(/Docker/)).not.toBeInTheDocument()
    expect(screen.queryByLabelText('Tarama aralığı (dakika)')).not.toBeInTheDocument()
    expect(within(details).getByText(/Tarama aralığı \(dakika\)/)).toBeInTheDocument()
    expect(within(details).getByText('360')).toBeInTheDocument()
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
  it('renders a permission state on 403', async () => {
    vi.mocked(getDiscoveryInstances).mockRejectedValue(new ApiError(403))
    mount()
    expect(await screen.findByText(/görüntüleme yetkiniz yok/)).toBeInTheDocument()
  })
  it('renders a load error without backend text', async () => {
    vi.mocked(getDiscoveryInstances).mockRejectedValue(new ApiError(500, { message: 'secret stack' }))
    mount()
    expect(await screen.findByText(/Sunucu isteği tamamlayamadı/)).toBeInTheDocument()
    expect(screen.queryByText(/secret stack/)).not.toBeInTheDocument()
  })
  it('passes filters to the API', async () => {
    mount()
    await screen.findByRole('link', { name: 'SQL Server (default)' })
    await userEvent.click(screen.getByText('Veritabanı bulma filtreleri', { selector: 'summary' }))
    fireEvent.change(screen.getByLabelText('Veritabanı türü'), { target: { value: 'mssql' } })
    fireEvent.click(screen.getByLabelText('Bulunamayanları da göster'))
    fireEvent.change(screen.getByLabelText('Bilgisayar'), { target: { value: 'agent-a' } })
    await waitFor(() => expect(getDiscoveryInstances).toHaveBeenLastCalledWith('tenant-a', { agentId: 'agent-a', engine: 'mssql', includeMissing: true }, expect.anything()))
  })
  describe('after a computer was added in the first-backup flow', () => {
    const added = { pathname: '/', state: { addedAgentId: 'agent-a' } }
    beforeEach(() => {
      vi.mocked(getAgents).mockResolvedValue([supportedAgent] as never)
      vi.mocked(rescanDiscoveryAgent).mockReset().mockResolvedValue(makeStatus())
    })

    it('scans the computer once and says it is scanning', async () => {
      vi.mocked(getDiscoveryAgents).mockResolvedValue({ items: [makeStatus({ scanState: 'Running' })] })
      mount(added)
      expect(await screen.findByText('Bilgisayar eklendi. Veritabanları taranıyor, birkaç saniye sürer.')).toBeInTheDocument()
      await waitFor(() => expect(rescanDiscoveryAgent).toHaveBeenCalledWith('tenant-a', 'agent-a'))
      expect(rescanDiscoveryAgent).toHaveBeenCalledTimes(1)
    })
    it('waits for the computer to report what it can do before scanning', async () => {
      vi.mocked(getAgents).mockResolvedValue([{ ...supportedAgent, capabilities: [], capabilitiesReportedAt: null }] as never)
      mount(added)
      expect(await screen.findByText(/Veritabanları taranıyor/)).toBeVisible()
      expect(rescanDiscoveryAgent).not.toHaveBeenCalled()
    })
    it('sends the user to a server that needs login details', async () => {
      vi.mocked(getDiscoveryInstances).mockResolvedValue({ items: [makeInstance({ access: 'CredentialsRequired', databaseCounts: { total: 0, user: 0, backupEligible: 0 } })] })
      mount(added)
      const notice = (await screen.findByText(/giriş bilgilerini girin/)).closest('[role=status]') as HTMLElement
      expect(within(notice).getByRole('link', { name: 'SQL Server (default)' })).toHaveAttribute('href', '/tenants/tenant-a/discovery/instances/inst-1')
    })
    it('offers a backup job from the usable server', async () => {
      mount(added)
      expect(await screen.findByText('Yedeklemek istediğiniz veritabanını seçin.')).toBeVisible()
      expect(screen.getByRole('link', { name: 'Yedekleme işi oluştur' })).toHaveAttribute('href', '/tenants/tenant-a/backup-jobs/new?discoveryInstance=inst-1')
    })
    it('offers manual configuration when nothing was found, and the notice can be dismissed', async () => {
      vi.mocked(getDiscoveryInstances).mockResolvedValue({ items: [] })
      mount(added)
      expect(await screen.findByText(/veritabanı bulunamadı; dosya yedeklemek/)).toBeVisible()
      expect(screen.getByRole('link', { name: 'Elle yapılandır' })).toHaveAttribute('href', '/tenants/tenant-a/backup-jobs/new')
      await userEvent.click(screen.getByRole('button', { name: 'Kapat' }))
      expect(screen.queryByRole('link', { name: 'Elle yapılandır' })).not.toBeInTheDocument()
    })
    it('offers the way back to the first-backup flow in every state of the guide', async () => {
      vi.mocked(getDiscoveryAgents).mockResolvedValue({ items: [makeStatus({ scanState: 'Running' })] })
      mount(added)
      expect(await screen.findByRole('link', { name: 'İlk yedeği almaya devam et' })).toHaveAttribute('href', '/tenants/tenant-a/first-backup')
      cleanup()
      vi.mocked(getDiscoveryAgents).mockResolvedValue({ items: [makeStatus()] })
      mount(added)
      await screen.findByText('Yedeklemek istediğiniz veritabanını seçin.')
      expect(screen.getByRole('link', { name: 'İlk yedeği almaya devam et' })).toHaveAttribute('href', '/tenants/tenant-a/first-backup')
      cleanup()
      vi.mocked(getDiscoveryInstances).mockResolvedValue({ items: [] })
      mount(added)
      await screen.findByText(/veritabanı bulunamadı; dosya yedeklemek/)
      expect(screen.getByRole('link', { name: 'İlk yedeği almaya devam et' })).toHaveAttribute('href', '/tenants/tenant-a/first-backup')
    })
    it('does not turn a throttled automatic scan into an error', async () => {
      vi.mocked(rescanDiscoveryAgent).mockRejectedValue(new ApiError(429, { error: 'rescan_too_soon' }))
      vi.mocked(getDiscoveryAgents).mockResolvedValue({ items: [makeStatus({ scanState: 'Running' })] })
      mount(added)
      expect(await screen.findByText('Bilgisayar eklendi. Veritabanları taranıyor, birkaç saniye sürer.')).toBeInTheDocument()
      await waitFor(() => expect(rescanDiscoveryAgent).toHaveBeenCalled())
      expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    })
    it.each([
      ['a scan that is running', { items: [makeStatus({ scanState: 'Running', lastScanAt: null, lastOutcome: null })] }],
      ['no scan report from the computer yet', { items: [] }],
    ])('shows the scan state, not "no match" with "Filtreleri temizle", for %s', async (_, statuses) => {
      vi.mocked(getDiscoveryAgents).mockResolvedValue(statuses)
      vi.mocked(getDiscoveryInstances).mockResolvedValue({ items: [] })
      mount(added)
      expect(await screen.findByText('Veritabanları aranıyor…')).toBeInTheDocument()
      expect(screen.queryByText(/Seçilen filtrelere uyan/)).not.toBeInTheDocument()
      expect(screen.queryByRole('button', { name: 'Filtreleri temizle' })).not.toBeInTheDocument()
      expect(screen.getByText('Veritabanı bulma filtreleri', { selector: 'summary' }).parentElement).not.toHaveAttribute('open')
    })
    it('says nothing was found, still without a filter button, once the first scan is over', async () => {
      vi.mocked(getDiscoveryInstances).mockResolvedValue({ items: [] })
      mount(added)
      expect(await screen.findByText(/henüz veritabanı bulunamadı/)).toBeInTheDocument()
      expect(screen.queryByText('Veritabanları aranıyor…')).not.toBeInTheDocument()
      expect(screen.queryByRole('button', { name: 'Filtreleri temizle' })).not.toBeInTheDocument()
    })
    it('shows the normal page without the route state', async () => {
      mount()
      await screen.findByRole('link', { name: 'SQL Server (default)' })
      expect(screen.queryByRole('button', { name: 'Kapat' })).not.toBeInTheDocument()
      expect(rescanDiscoveryAgent).not.toHaveBeenCalled()
    })
  })
})
