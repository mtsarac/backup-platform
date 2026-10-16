import { fireEvent, screen, waitFor } from '@testing-library/react'
import { render } from '@/test/render'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { getDiscoveryAgents, getDiscoveryInstance, verifyDiscoveryInstance } from '@/features/discovery/api'
import { makeDetail, makeStatus } from '@/features/discovery/test-fixtures'
import { ApiError } from '@/lib/api/client'
import { InstanceDetailPage } from './instance-detail-page'

const access = vi.hoisted(() => ({ tenantId: 'tenant-a', tenant: { name: 'Acme' }, canOperate: true, canAdmin: true }))
vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => access }))
vi.mock('@/features/discovery/api')

function Target() { const location = useLocation(); return <p data-testid="target">{location.pathname}{location.search}</p> }
function mount() {
  render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })}><MemoryRouter initialEntries={['/tenants/tenant-a/discovery/instances/inst-1']}><Routes>
    <Route path="/tenants/:tenantId/discovery/instances/:instanceId" element={<InstanceDetailPage />} /><Route path="/tenants/:tenantId/backup-jobs/new" element={<Target />} />
  </Routes></MemoryRouter></QueryClientProvider>)
}
beforeEach(() => {
  vi.clearAllMocks()
  Object.assign(access, { canOperate: true, canAdmin: true })
  vi.mocked(getDiscoveryAgents).mockResolvedValue({ items: [makeStatus()] })
  vi.mocked(getDiscoveryInstance).mockResolvedValue(makeDetail())
})

describe('instance detail page', () => {
  it('does not claim databases exist when credentials are required', async () => {
    vi.mocked(getDiscoveryInstance).mockResolvedValue(makeDetail({ access: 'CredentialsRequired', databases: [], credential: { bound: false, credentialReference: null, integratedSecurity: false, encrypt: true, trustServerCertificate: false } }))
    mount()
    expect(await screen.findByText(/veritabanları listelenmedi/)).toBeInTheDocument()
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Yedekleme İşi Oluştur/ })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Bağlantıyı Doğrula' })).toBeDisabled()
  })
  it('lets only selectable databases be picked and navigates to the wizard with them', async () => {
    mount()
    expect(await screen.findByLabelText('ERP seç')).toBeEnabled()
    expect(screen.getByLabelText('master seç')).toBeDisabled()
    expect(screen.getByLabelText('Covered seç')).toBeDisabled()
    expect(screen.getByRole('link', { name: "Covered job" })).toHaveAttribute('href', '/tenants/tenant-a/backup-jobs/job-9/edit')
    expect(screen.getByRole('button', { name: /Yedekleme İşi Oluştur/ })).toBeDisabled()
    fireEvent.click(screen.getByLabelText('ERP seç'))
    fireEvent.click(screen.getByLabelText('CRM seç'))
    fireEvent.click(screen.getByRole('button', { name: /Yedekleme İşi Oluştur/ }))
    expect(await screen.findByTestId('target')).toHaveTextContent('/tenants/tenant-a/backup-jobs/new?discoveryInstance=inst-1&database=ERP&database=CRM')
  })
  it('asks for the database user and password, never shows a stored one, and verifies the connection', async () => {
    vi.mocked(verifyDiscoveryInstance).mockRejectedValue(new ApiError(409, { error: 'discovery_unsupported' }))
    mount()
    expect(await screen.findByLabelText('Veritabanı kullanıcı adı')).toHaveValue('')
    expect(screen.getByLabelText('Veritabanı parolası')).toHaveValue('')
    fireEvent.click(screen.getByRole('button', { name: 'Bağlantıyı Doğrula' }))
    await waitFor(() => expect(verifyDiscoveryInstance).toHaveBeenCalledWith('tenant-a', 'inst-1'))
    expect(await screen.findByText(/Ajan sürümü keşfi desteklemiyor/)).toBeInTheDocument()
  })
  it('is read-only for a Viewer', async () => {
    Object.assign(access, { canOperate: false, canAdmin: false })
    mount()
    await screen.findByText('CRM')
    expect(screen.queryByLabelText('ERP seç')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Kaydet' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Bağlantıyı Doğrula' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Yedekleme İşi Oluştur/ })).not.toBeInTheDocument()
  })
})
