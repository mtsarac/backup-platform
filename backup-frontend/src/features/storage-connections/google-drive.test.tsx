import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import * as api from './api'
import { makeConnection, tenantAdminPermissions } from './test-fixtures'
import { ConnectionDetail } from './connection-detail'
import type { ConnectionView, ProviderDescriptor } from './types'

vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => ({ tenantId: 'tenant-a', canAdmin: true, canOperate: true }) }))
vi.mock('./api')

const googleDriveProvider: ProviderDescriptor = {
  providerType: 'google-drive', displayName: 'Google Drive', availability: 'Available', topology: 'BackendRelayed', executionLocation: 'Backend',
  capabilities: ['storage.read', 'storage.delete', 'storage.atomic-publish', 'storage.verify-absence'], supportedOwnerships: ['TenantManaged'],
  tenantCreatable: true, platformCreatable: false, maxConnectionsPerTenant: null,
  configFields: [{ name: 'accountEmail', label: 'Google hesabı', kind: 'Text', required: false, maxLength: 256 }, { name: 'folderId', label: 'Klasör ID', kind: 'Text', required: false, maxLength: 128 }],
  secretFields: [{ name: 'refreshToken', label: 'Refresh token', required: false }], testMode: 'BackendRoundTrip',
}

function mount(overrides: Partial<ConnectionView> = {}) {
  const connection = makeConnection({ id: 'drive', name: 'Google Drive Yedekleri', providerType: 'google-drive', providerDisplayName: 'Google Drive', permissions: { ...tenantAdminPermissions }, ...overrides })
  vi.mocked(api.getTenantConnections).mockResolvedValue({ items: [connection] })
  vi.mocked(api.getTenantProviders).mockResolvedValue({ items: [googleDriveProvider] })
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(<QueryClientProvider client={client}><MemoryRouter initialEntries={['/storage/drive?tab=credentials']}><Routes><Route path="/storage/:connectionId" element={<ConnectionDetail />} /></Routes></MemoryRouter></QueryClientProvider>)
}
const panel = () => within(screen.getByRole('tabpanel'))
beforeEach(() => vi.resetAllMocks())

describe('Google Drive storage destination', () => {
  it('displays connected state and account without exposing OAuth credentials', async () => {
    mount({ hasSecret: true, configuration: { accountEmail: 'backup-user@gmail.com', reauthorizationRequired: 'false' } })
    expect(await screen.findByText('Bağlı')).toBeInTheDocument()
    expect(panel().getByText('backup-user@gmail.com')).toBeInTheDocument()
    expect(panel().getByRole('button', { name: /yeniden bağla/i })).toBeInTheDocument()
    expect(panel().getByRole('button', { name: /bağlantıyı kes/i })).toBeInTheDocument()
    expect(screen.queryByLabelText('Refresh token')).not.toBeInTheDocument()
  })

  it('requires reauthorization when disconnected', async () => {
    mount({ hasSecret: false, configuration: { reauthorizationRequired: 'true' } })
    await screen.findByRole('heading', { level: 1, name: 'Google Drive Yedekleri' })
    expect(panel().getByText('Yeniden yetkilendirme gerekli')).toBeInTheDocument()
    expect(panel().getByRole('button', { name: /google hesabını bağla/i })).toBeInTheDocument()
  })

  it('starts the real authorization URL flow from the connection', async () => {
    vi.mocked(api.getGoogleDriveAuthorizeUrl).mockResolvedValue({ authorizationUrl: 'https://accounts.google.com/o/oauth2/v2/auth?mock=1' })
    const popup = vi.spyOn(window, 'open').mockReturnValue(null)
    mount({ hasSecret: false })
    await userEvent.click(await screen.findByRole('button', { name: /google hesabını bağla/i }))
    await waitFor(() => expect(api.getGoogleDriveAuthorizeUrl).toHaveBeenCalledWith('tenant-a', 'drive'))
    expect(popup).toHaveBeenCalled()
    popup.mockRestore()
  })

  it('requires confirmation and warns of shared-grant revocation before disconnect', async () => {
    vi.mocked(api.disconnectGoogleDrive).mockResolvedValue({ disconnected: true })
    mount({ hasSecret: true, configuration: { accountEmail: 'test@gmail.com' } })
    await userEvent.click(await screen.findByRole('button', { name: /bağlantıyı kes/i }))
    const dialog = await screen.findByRole('dialog')
    expect(dialog).toHaveTextContent(/Google bağlantısı kesilsin mi/i)
    expect(dialog).toHaveTextContent(/aynı Google hesabını ve uygulamayı kullanan diğer bağlantıların da erişimi iptal olabilir/i)
    expect(api.disconnectGoogleDrive).not.toHaveBeenCalled()
    await userEvent.click(within(dialog).getByRole('button', { name: 'Bağlantıyı kes' }))
    await waitFor(() => expect(api.disconnectGoogleDrive).toHaveBeenCalledWith('tenant-a', 'drive'))
  })
})
