import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import * as api from './api'
import { catalog, makeConnection, tenantAdminPermissions } from './test-fixtures'
import { TenantDestinations } from './tenant-destinations'
import type { ConnectionView, ProviderDescriptor } from './types'

const access = vi.hoisted(() => ({ tenantId: 'tenant-a', canAdmin: true, canOperate: true }))
vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => access }))
vi.mock('./api')

const googleDriveProvider: ProviderDescriptor = {
  providerType: 'google-drive',
  displayName: 'Google Drive',
  availability: 'Available',
  topology: 'BackendRelayed',
  executionLocation: 'Backend',
  capabilities: ['storage.read', 'storage.delete', 'storage.atomic-publish', 'storage.verify-absence'],
  supportedOwnerships: ['TenantManaged'],
  tenantCreatable: true,
  platformCreatable: false,
  maxConnectionsPerTenant: null,
  configFields: [
    { name: 'accountEmail', label: 'Google hesabı', kind: 'Text', required: false, maxLength: 256 },
    { name: 'folderId', label: 'Klasör ID', kind: 'Text', required: false, maxLength: 128 },
  ],
  secretFields: [{ name: 'refreshToken', label: 'Refresh token', required: false }],
  testMode: 'BackendRoundTrip',
}

function mount() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  const view = render(<QueryClientProvider client={client}><TenantDestinations /></QueryClientProvider>)
  return { client, ...view }
}

function serve(items: ConnectionView[], providers = [...catalog, googleDriveProvider]) {
  vi.mocked(api.getTenantConnections).mockResolvedValue({ items })
  vi.mocked(api.getTenantProviders).mockResolvedValue({ items: providers })
}

const article = (name: string) => within(screen.getByRole('article', { name }))

beforeEach(() => {
  vi.resetAllMocks()
  access.canAdmin = true
})

describe('Google Drive storage destination', () => {
  it('displays connected state with account email when secret is present', async () => {
    const driveConn = makeConnection({
      id: 'conn-gdrive',
      name: 'Google Drive Yedekleri',
      providerType: 'google-drive',
      providerDisplayName: 'Google Drive',
      hasSecret: true,
      configuration: { accountEmail: 'backup-user@gmail.com', reauthorizationRequired: 'false' },
      permissions: { ...tenantAdminPermissions },
    })

    serve([driveConn])
    mount()

    await screen.findByRole('article', { name: 'Google Drive Yedekleri' })
    const card = article('Google Drive Yedekleri')

    expect(card.getByText('Bağlı')).toBeInTheDocument()
    expect(card.getByText('backup-user@gmail.com')).toBeInTheDocument()
    expect(card.getByRole('button', { name: /yeniden bağla/i })).toBeInTheDocument()
    expect(card.getByRole('button', { name: /bağlantıyı kes/i })).toBeInTheDocument()
  })

  it('displays reauthorization required badge when not connected or reauth requested', async () => {
    const driveConn = makeConnection({
      id: 'conn-gdrive-reauth',
      name: 'Google Drive Yeniden',
      providerType: 'google-drive',
      providerDisplayName: 'Google Drive',
      hasSecret: false,
      configuration: { reauthorizationRequired: 'true' },
      permissions: { ...tenantAdminPermissions },
    })

    serve([driveConn])
    mount()

    await screen.findByRole('article', { name: 'Google Drive Yeniden' })
    const card = article('Google Drive Yeniden')

    expect(card.getByText('Yeniden yetkilendirme gerekli')).toBeInTheDocument()
    expect(card.getByRole('button', { name: /google hesabını bağla/i })).toBeInTheDocument()
  })

  it('triggers authorization URL request when clicking connect button', async () => {
    const user = userEvent.setup()
    const driveConn = makeConnection({
      id: 'conn-gdrive-connect',
      name: 'Drive Connect',
      providerType: 'google-drive',
      providerDisplayName: 'Google Drive',
      hasSecret: false,
      configuration: {},
      permissions: { ...tenantAdminPermissions },
    })

    serve([driveConn])
    vi.mocked(api.getGoogleDriveAuthorizeUrl).mockResolvedValue({
      authorizationUrl: 'https://accounts.google.com/o/oauth2/v2/auth?mock=1',
    })
    const popupSpy = vi.spyOn(window, 'open').mockReturnValue(null)

    mount()

    await screen.findByRole('article', { name: 'Drive Connect' })
    const card = article('Drive Connect')

    const connectBtn = card.getByRole('button', { name: /google hesabını bağla/i })
    await user.click(connectBtn)

    await waitFor(() => {
      expect(api.getGoogleDriveAuthorizeUrl).toHaveBeenCalledWith('tenant-a', 'conn-gdrive-connect')
    })
    expect(popupSpy).toHaveBeenCalled()
    popupSpy.mockRestore()
  })

  it('disconnects Google Drive account on confirmation', async () => {
    const user = userEvent.setup()
    const driveConn = makeConnection({
      id: 'conn-gdrive-disc',
      name: 'Drive Disc',
      providerType: 'google-drive',
      providerDisplayName: 'Google Drive',
      hasSecret: true,
      configuration: { accountEmail: 'test@gmail.com' },
      permissions: { ...tenantAdminPermissions },
    })

    serve([driveConn])
    vi.mocked(api.disconnectGoogleDrive).mockResolvedValue({ disconnected: true })

    mount()

    await screen.findByRole('article', { name: 'Drive Disc' })
    const card = article('Drive Disc')

    const discBtn = card.getByRole('button', { name: /bağlantıyı kes/i })
    await user.click(discBtn)

    const dialog = await screen.findByRole('dialog')
    expect(dialog).toHaveTextContent(/Google bağlantısı kesilsin mi/i)

    const confirmBtn = within(dialog).getByRole('button', { name: 'Bağlantıyı kes' })
    await user.click(confirmBtn)

    await waitFor(() => {
      expect(api.disconnectGoogleDrive).toHaveBeenCalledWith('tenant-a', 'conn-gdrive-disc')
    })
  })
})
