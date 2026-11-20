import { cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react'
import { render } from '@/test/render'
import { setViewport } from '@/test/viewport'
import type { ReactNode } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ArtifactDetail } from '@/pages/artifacts/artifact-detail-page'
import { SharesPage } from '@/pages/shares/shares-page'
import { PlatformSharesPage } from '@/pages/platform/platform-shares-page'
import { ApiError, apiRequest } from '@/lib/api/client'
import type * as ApiClient from '@/lib/api/client'
import type { BackupArtifact } from '@/features/artifacts/types'
import type { BackupShareView, DownloadAttemptView } from './types'
vi.mock('@/features/artifacts/artifact-run-link', () => ({ ArtifactRunLink: ({ children }: { children: ReactNode }) => <span>{children}</span> }))

vi.mock('@/lib/api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof ApiClient>()
  return {
    ...actual,
    apiRequest: vi.fn(),
    API_BASE_URL: '/api',
    tenantPath: (tenantId: string, path: string) => `tenants/${tenantId}/${path}`,
    isForbidden: () => false,
  }
})

const permissions = vi.hoisted(() => ({ role: 'UserAdmin' }))
vi.mock('@/features/tenancy/use-tenant', () => ({
  useTenant: () => ({ tenantId: 'tenant-a' }),
}))
vi.mock('@/features/auth/use-auth', () => ({
  useAuth: () => ({ user: { role: permissions.role } }),
}))

const testArtifact: BackupArtifact = {
  id: 'artifact-1',
  sizeBytes: 1024,
  sourceChecksum: 'src123',
  artifactChecksum: 'art123',
  checksumAlgorithm: 'sha256',
  createdAt: '2026-11-11T10:00:00Z',
  state: 'Available',
  acceptedAt: null,
  retentionExpiresAt: null,
  retentionPinnedAt: null,
  deleteAfter: null,
  deleteReason: null,
  runId: 'run-1',
  jobId: 'job-1',
  attemptId: 'attempt-1',
  jobName: 'Daily Backup',
  databaseName: 'ProductionDb',
  replicas: [
    {
      id: 'replica-1',
      storageProvider: 'central',
      storageConnectionId: 'conn-1',
      storageTarget: 'default',
      key: 'test.bak',
      sizeBytes: 1024,
      checksum: 'art123',
      checksumAlgorithm: 'sha256',
      verificationState: 'Verified',
      verificationMethod: 'ReadBack',
      createdAt: '2026-11-11T10:00:00Z',
      verifiedAt: '2026-11-11T10:01:00Z',
      status: 'Available',
      usable: true,
      deleteRequestedAt: null,
      deleteAfter: null,
      deleteReason: null,
      deletedAt: null,
      deleteAttempts: 0,
      deleteFailureCode: null,
      deleteNextAttemptAt: null,
      deletionExecutor: 'Backend',
      waitingForAgent: false,
    },
  ],
}

const testShare: BackupShareView = {
  id: 'share-1',
  tenantId: 'tenant-a',
  artifactId: 'artifact-1',
  createdByUserId: 'user-admin-1',
  createdByRole: 'UserAdmin',
  reason: 'Audit inspection',
  createdAtUtc: '2026-11-11T12:00:00Z',
  expiresAtUtc: '2026-11-12T12:00:00Z',
  revokedAtUtc: null,
  revokedByUserId: null,
  status: 'Active',
  artifactName: 'ProductionDb',
  createdByDisplayName: 'Admin Alice',
}

const expiredShare: BackupShareView = {
  ...testShare,
  id: 'share-2',
  status: 'Expired',
  expiresAtUtc: '2026-11-10T12:00:00Z',
  reason: 'Past diagnostic',
}

const revokedShare: BackupShareView = {
  ...testShare,
  id: 'share-3',
  status: 'Revoked',
  revokedAtUtc: '2026-11-11T14:00:00Z',
  revokedByUserId: 'user-admin-1',
  reason: 'Cancelled early',
}

const platformShare: BackupShareView = {
  id: 'share-platform-1',
  tenantId: 'tenant-b',
  tenantName: 'Acme Corp',
  artifactId: 'artifact-2',
  createdByUserId: 'platform-user-1',
  createdByRole: 'PlatformAdmin',
  reason: 'Platform incident investigation',
  createdAtUtc: '2026-11-11T13:00:00Z',
  expiresAtUtc: '2026-11-12T13:00:00Z',
  revokedAtUtc: null,
  revokedByUserId: null,
  status: 'Active',
  artifactName: 'AnalyticsDb',
  createdByDisplayName: 'Super Admin',
}

const platformAdminShare: BackupShareView = {
  ...testShare,
  id: 'share-pa',
  createdByUserId: 'platform-admin-1',
  createdByRole: 'PlatformAdmin',
  createdByDisplayName: 'Platform Admin Peter',
}

const completedAttempt: DownloadAttemptView = {
  id: 'attempt-completed-1',
  shareId: 'share-1',
  tenantId: 'tenant-a',
  tenantName: 'Tenant A',
  artifactId: 'artifact-1',
  artifactName: 'ProductionDb',
  initiatingUserId: 'platform-user-1',
  initiatingUserDisplayName: 'Super Admin',
  startedAtUtc: '2026-11-11T14:00:00Z',
  endedAtUtc: '2026-11-11T14:01:00Z',
  result: 'Completed',
  bytesTransferred: 2048,
  failureReason: null,
  decrypted: true,
}

const refusedAttempt: DownloadAttemptView = {
  id: 'attempt-refused-1',
  shareId: 'share-3',
  tenantId: 'tenant-a',
  tenantName: 'Tenant A',
  artifactId: 'artifact-1',
  artifactName: 'ProductionDb',
  initiatingUserId: 'platform-user-1',
  initiatingUserDisplayName: 'Super Admin',
  startedAtUtc: '2026-11-11T15:00:00Z',
  endedAtUtc: '2026-11-11T15:00:00Z',
  result: 'Refused',
  bytesTransferred: 0,
  failureReason: 'share_revoked',
  decrypted: false,
}

const failedAttempt: DownloadAttemptView = {
  id: 'attempt-failed-1',
  shareId: 'share-platform-1',
  tenantId: 'tenant-b',
  tenantName: 'Acme Corp',
  artifactId: 'artifact-2',
  artifactName: 'AnalyticsDb',
  initiatingUserId: 'platform-user-1',
  initiatingUserDisplayName: 'Super Admin',
  startedAtUtc: '2026-11-11T16:00:00Z',
  endedAtUtc: '2026-11-11T16:00:30Z',
  result: 'Failed',
  bytesTransferred: 512,
  failureReason: 'integrity_mismatch',
  decrypted: false,
}
const request = vi.mocked(apiRequest)

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

beforeEach(() => {
  permissions.role = 'UserAdmin'
})

async function renderDetailPage() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  })
  render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/tenants/tenant-a/artifacts/artifact-1']}>
        <Routes>
          <Route path="/tenants/:tenantId/artifacts/:id" element={<ArtifactDetail id="artifact-1" />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>
  )
  fireEvent.click(await screen.findByRole('button', { name: 'Paylaş' }))
  return queryClient
}
function LocationSearch() {
  return <output data-testid="location-search">{useLocation().search}</output>
}


const attemptPage = (items: DownloadAttemptView[], totalCount = items.length) => ({ items, totalCount, page: 1, pageSize: 25 })
const attemptCalls = () => request.mock.calls.map(call => String(call[0])).filter(path => path.includes('download-attempts'))

function renderSharesPage(path = '/tenants/tenant-a/backup-shares') {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  })
  render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[path]}>
        <LocationSearch />
        <Routes>
          <Route path="/tenants/:tenantId/backup-shares" element={<SharesPage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>
  )
  return queryClient
}

function renderPlatformSharesPage(path = '/platform/shares') {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  })
  render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[path]}>
        <LocationSearch />
        <Routes>
          <Route path="/platform/shares" element={<PlatformSharesPage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>
  )
  return queryClient
}

describe('BackupShare frontend', () => {
  it('renders artifact detail share section and existing shares', async () => {
    request.mockImplementation(async (path: string) => {
      if (path.includes('backup-shares') || path.includes('/shares')) return [testShare]
      if (path.includes('repairs')) return []
      if (path.endsWith('backup-artifacts/artifact-1')) return testArtifact
      return path.includes('download-attempts') ? { items: [], totalCount: 0, page: 1, pageSize: 25 } : []
    })

    await renderDetailPage()

    await waitFor(() => {
      expect(screen.getByText('Paylaşımlar')).toBeInTheDocument()
      expect(screen.getByText('Audit inspection')).toBeInTheDocument()
      expect(screen.getByText('Admin Alice')).toBeInTheDocument()
      expect(screen.getByText('UserAdmin')).toBeInTheDocument()
      expect(screen.getByText('Aktif')).toBeInTheDocument()
    })
  })

  it('creates a share from artifact detail with default duration', async () => {
    request.mockImplementation(async (path: string, options?: { method?: string; json?: unknown }) => {
      if (options?.method === 'POST') {
        return { ...testShare, reason: 'Custom check' }
      }
      if (path.includes('backup-shares') || path.includes('/shares')) return []
      if (path.includes('repairs')) return []
      if (path.endsWith('backup-artifacts/artifact-1')) return testArtifact
      return path.includes('download-attempts') ? { items: [], totalCount: 0, page: 1, pageSize: 25 } : []
    })

    await renderDetailPage()

    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Paylaşım oluştur' })).toBeInTheDocument()
    })

    fireEvent.click(screen.getByRole('button', { name: 'Paylaşım oluştur' }))

    await waitFor(() => {
      expect(screen.getByText('Yeni paylaşım oluştur')).toBeInTheDocument()
    })

    const reasonInput = screen.getByPlaceholderText('Paylaşım gerekçesini yazın')
    fireEvent.change(reasonInput, { target: { value: 'Custom check' } })

    const submitButtons = screen.getAllByRole('button', { name: 'Paylaşım oluştur' })
    fireEvent.click(submitButtons[submitButtons.length - 1])

    await waitFor(() => {
      expect(request).toHaveBeenCalledWith(
        'tenants/tenant-a/backup-artifacts/artifact-1/shares',
        expect.objectContaining({
          method: 'POST',
          json: { durationHours: 24, reason: 'Custom check' },
        })
      )
    })
  })

  it('offers only 1 saat / 24 saat / 3 gün when creating, with no free number field', async () => {
    request.mockImplementation(async (path: string) => {
      if (path.includes('backup-shares') || path.includes('/shares')) return []
      if (path.includes('repairs')) return []
      if (path.endsWith('backup-artifacts/artifact-1')) return testArtifact
      return path.includes('download-attempts') ? { items: [], totalCount: 0, page: 1, pageSize: 25 } : []
    })

    await renderDetailPage()
    fireEvent.click(await screen.findByRole('button', { name: 'Paylaşım oluştur' }))

    const select = await screen.findByRole('combobox', { name: 'Süre' })
    expect(within(select).getAllByRole('option').map((option) => option.textContent)).toEqual(['1 saat', '24 saat', '3 gün'])
    expect(screen.queryByRole('spinbutton')).not.toBeInTheDocument()
    fireEvent.change(select, { target: { value: '72' } })
    const submitButtons = screen.getAllByRole('button', { name: 'Paylaşım oluştur' })
    fireEvent.click(submitButtons[submitButtons.length - 1])
    await waitFor(() => expect(request).toHaveBeenCalledWith('tenants/tenant-a/backup-artifacts/artifact-1/shares', expect.objectContaining({ method: 'POST', json: { durationHours: 72, reason: undefined } })))
  })

  it('disables share creation when artifact has no verified available replica', async () => {
    const unverifiedArtifact = {
      ...testArtifact,
      replicas: [{ ...testArtifact.replicas[0], status: 'Deleted' }],
    }

    request.mockImplementation(async (path: string) => {
      if (path.includes('backup-shares') || path.includes('/shares')) return []
      if (path.includes('repairs')) return []
      if (path.endsWith('backup-artifacts/artifact-1')) return unverifiedArtifact
      return path.includes('download-attempts') ? { items: [], totalCount: 0, page: 1, pageSize: 25 } : []
    })

    await renderDetailPage()

    await waitFor(() => {
      const button = screen.getByRole('button', { name: 'Paylaşım oluştur' })
      expect(button).toBeDisabled()
      expect(
        screen.getByText('İndirilebilir doğrulanmış bir kopya bulunmadığından paylaşım oluşturulamaz.')
      ).toBeInTheDocument()
    })
  })

  it('renders Paylaşımlarım page with active, expired, and revoked shares', async () => {
    request.mockImplementation(async (path: string) => (path.includes('download-attempts') ? { items: [], totalCount: 0, page: 1, pageSize: 25 } : [testShare, expiredShare, revokedShare]))

    renderSharesPage()

    await waitFor(() => {
      expect(screen.getByRole('table', { name: 'Paylaşımlarım' })).toBeInTheDocument()
      expect(screen.getAllByText('ProductionDb')).toHaveLength(3)
      expect(within(screen.getByRole('table', { name: 'Paylaşımlarım' })).getByText('Aktif')).toBeInTheDocument()
      expect(within(screen.getByRole('table', { name: 'Paylaşımlarım' })).getByText('Süresi doldu')).toBeInTheDocument()
      expect(within(screen.getByRole('table', { name: 'Paylaşımlarım' })).getByText('İptal edildi')).toBeInTheDocument()
    })
  })

  it('renders empty state in Paylaşımlarım when no shares exist', async () => {
    request.mockImplementation(async (path: string) => (path.includes('download-attempts') ? { items: [], totalCount: 0, page: 1, pageSize: 25 } : []))

    renderSharesPage()

    await waitFor(() => {
      expect(screen.getByText("Henüz paylaşım yok. Bir yedeğin ayrıntısından Platform Yöneticisi'ne süreli indirme izni verebilirsiniz.")).toBeInTheDocument()
      expect(screen.getByRole('link', { name: 'Yedek dosyalarını aç' })).toHaveAttribute('href', '/tenants/tenant-a/backup-runs')
    })
  })

  it('shows PlatformAdmin-created shares in Paylaşımlarım as read-only with PlatformAdmin role', async () => {
    request.mockImplementation(async (path: string) => (path.includes('download-attempts') ? { items: [], totalCount: 0, page: 1, pageSize: 25 } : [platformShare]))

    renderSharesPage()

    await waitFor(() => {
      expect(screen.getByText('AnalyticsDb')).toBeInTheDocument()
      expect(screen.getByText('Platform incident investigation')).toBeInTheDocument()
      expect(screen.getByText('Super Admin')).toBeInTheDocument()
      expect(screen.getByText('Platform Yöneticisi')).toBeInTheDocument()
      expect(within(screen.getByRole('table', { name: 'Paylaşımlarım' })).getByText('Aktif')).toBeInTheDocument()
      // Ensure tenant view is read-only (no extend or revoke buttons exist)
      expect(screen.queryByRole('button', { name: /uzat|iptal/i })).not.toBeInTheDocument()
    })
  })

  it('renders Paylaşılan Yedekler page with cross-tenant shares, tenant column, and status filter', async () => {
    permissions.role = 'PlatformAdmin'
    request.mockImplementation(async (path: string) => {
      if (path.includes('platform/backup-shares')) return [platformShare, testShare]
      if (path.includes('platform/tenants')) {
        return {
          items: [
            { tenantId: 'tenant-a', name: 'Tenant A' },
            { tenantId: 'tenant-b', name: 'Acme Corp' },
          ],
          totalCount: 2,
          pageSize: 100,
          page: 1,
        }
      }
      return path.includes('download-attempts') ? { items: [], totalCount: 0, page: 1, pageSize: 25 } : []
    })

    renderPlatformSharesPage()

    await waitFor(() => {
      expect(screen.getByRole('heading', { name: 'Paylaşılan Yedekler' })).toBeInTheDocument()
      expect(screen.getByText('Acme Corp')).toBeInTheDocument()
      expect(screen.getByText('Platform incident investigation')).toBeInTheDocument()
      expect(screen.getByText('Super Admin')).toBeInTheDocument()
      expect(screen.getByText('Platform Yöneticisi')).toBeInTheDocument()
      expect(screen.getByRole('region', { name: 'Paylaşım filtreleri' })).toBeInTheDocument()
      expect(screen.getByRole('button', { name: 'Paylaşım oluştur' })).toBeInTheDocument()
    })
  })

  it('restores and updates client search, status and sorting on the shares page', async () => {
    request.mockImplementation(async (path: string) => path.includes('download-attempts') ? { items: [], totalCount: 0, page: 1, pageSize: 25 } : [testShare, { ...expiredShare, artifactName: 'ArchiveDb', createdAtUtc: '2026-11-13T12:00:00Z' }])
    renderSharesPage('/tenants/tenant-a/backup-shares?status=Expired&search=archive')
    const table = await screen.findByRole('table', { name: 'Paylaşımlarım' })
    expect(within(table).getByText('ArchiveDb')).toBeInTheDocument()
    expect(within(table).queryByText('ProductionDb')).not.toBeInTheDocument()
    fireEvent.change(screen.getByRole('searchbox', { name: 'Paylaşım ara' }), { target: { value: 'missing' } })
    fireEvent.blur(screen.getByRole('searchbox', { name: 'Paylaşım ara' }))
    expect(screen.getByText('Seçilen filtrelere uygun paylaşım bulunamadı.')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Filtreleri temizle' }))
    expect(screen.getByTestId('location-search')).toHaveTextContent('')
    const rows = within(screen.getByRole('table', { name: 'Paylaşımlarım' })).getAllByRole('row')
    expect(rows[1]).toHaveTextContent('ArchiveDb')
    fireEvent.click(within(rows[0]).getByRole('button', { name: /Yedek dosyası/ }))
    expect(screen.getByTestId('location-search')).toHaveTextContent('sort=artifact')
    expect(within(screen.getByRole('table', { name: 'Paylaşımlarım' })).getAllByRole('row')[1]).toHaveTextContent('ArchiveDb')
  })

  it('filters platform shares locally while retaining the tenant URL filter', async () => {
    permissions.role = 'PlatformAdmin'
    request.mockImplementation(async (path: string) => {
      if (path.includes('platform/backup-shares')) return [platformShare, { ...testShare, status: 'Expired' }]
      if (path.includes('platform/tenants')) return { items: [{ tenantId: 'tenant-b', name: 'Acme Corp' }], totalCount: 1, pageSize: 100, page: 1 }
      return path.includes('download-attempts') ? { items: [], totalCount: 0, page: 1, pageSize: 25 } : []
    })
    renderPlatformSharesPage('/platform/shares?tenantId=tenant-b&status=Expired')
    const table = await screen.findByRole('table', { name: 'Paylaşımlar' })
    expect(within(table).getByText('ProductionDb')).toBeInTheDocument()
    expect(within(table).queryByText('AnalyticsDb')).not.toBeInTheDocument()
    expect(request.mock.calls.filter(([path]) => String(path).includes('platform/backup-shares')).every(([path]) => !String(path).includes('status='))).toBe(true)
    fireEvent.change(screen.getByRole('combobox', { name: 'Durum' }), { target: { value: 'Active' } })
    expect(screen.getByTestId('location-search')).toHaveTextContent('tenantId=tenant-b')
    expect(screen.getByTestId('location-search')).toHaveTextContent('status=Active')
    expect(within(screen.getByRole('table', { name: 'Paylaşımlar' })).getByRole('link', { name: 'Acme Corp' })).toHaveAttribute('href', '/platform/tenants/tenant-b')
  })

  it('sorts share cards on narrow screens', async () => {
    setViewport(false)
    request.mockImplementation(async (path: string) => path.includes('download-attempts') ? { items: [], totalCount: 0, page: 1, pageSize: 25 } : [testShare, { ...expiredShare, artifactName: 'ArchiveDb', createdAtUtc: '2026-11-13T12:00:00Z' }])
    renderSharesPage()
    const sort = await screen.findByRole('combobox', { name: 'Sırala' })
    expect(sort).toHaveValue('createdAt:desc')
    fireEvent.change(sort, { target: { value: 'artifact:desc' } })
    expect(screen.getByTestId('location-search')).toHaveTextContent('sort=artifact&dir=desc')
    expect(screen.getAllByRole('heading', { level: 2 })[0]).toHaveTextContent('ProductionDb')
  })

  it('creates a share from Paylaşılan Yedekler screen via CreatePlatformShareDialog', async () => {
    permissions.role = 'PlatformAdmin'
    request.mockImplementation(async (path: string, options?: { method?: string; json?: unknown }) => {
      if (options?.method === 'POST') {
        return platformShare
      }
      if (path.includes('platform/backup-shares')) return []
      if (path.includes('platform/tenants')) {
        return {
          items: [{ tenantId: 'tenant-b', name: 'Acme Corp' }],
          totalCount: 1,
          pageSize: 100,
          page: 1,
        }
      }
      if (path.includes('tenants/tenant-b/backup-artifacts')) {
        return {
          items: [testArtifact],
          totalCount: 1,
          page: 1,
          pageSize: 100,
        }
      }
      return path.includes('download-attempts') ? { items: [], totalCount: 0, page: 1, pageSize: 25 } : []
    })

    renderPlatformSharesPage()

    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Paylaşım oluştur' })).toBeInTheDocument()
    })

    fireEvent.click(screen.getByRole('button', { name: 'Paylaşım oluştur' }))

    await waitFor(() => {
      expect(screen.getByText('Yeni paylaşım oluştur')).toBeInTheDocument()
    })

    // Select tenant
    const tenantInput = screen.getByRole('combobox', { name: 'Çalışma alanı' })
    fireEvent.change(tenantInput, { target: { value: 'Acme' } })

    await waitFor(() => {
      expect(screen.getByText('Acme Corp')).toBeInTheDocument()
    })
    fireEvent.click(screen.getByText('Acme Corp'))

    // Select artifact
    await waitFor(() => {
      expect(screen.getByRole('combobox', { name: 'Yedek dosyası' })).toBeInTheDocument()
    })
    const artifactInput = screen.getByRole('combobox', { name: 'Yedek dosyası' })
    fireEvent.change(artifactInput, { target: { value: 'ProductionDb' } })

    await waitFor(() => {
      expect(screen.getByText(/ProductionDb/)).toBeInTheDocument()
    })
    fireEvent.click(screen.getByText(/ProductionDb/))

    // Enter reason
    const reasonInput = screen.getByPlaceholderText('Paylaşım gerekçesini yazın')
    fireEvent.change(reasonInput, { target: { value: 'Platform urgent access' } })

    // Submit
    const submitButtons = screen.getAllByRole('button', { name: 'Paylaşım oluştur' })
    fireEvent.click(submitButtons[submitButtons.length - 1])

    await waitFor(() => {
      expect(request).toHaveBeenCalledWith(
        'platform/backup-shares',
        expect.objectContaining({
          method: 'POST',
          json: expect.objectContaining({
            tenantId: 'tenant-b',
            artifactId: 'artifact-1',
            durationHours: 24,
            reason: 'Platform urgent access',
          }),
        })
      )
    })
  })

  it('extends active share from Paylaşımlarım', async () => {
    request.mockImplementation(async (path: string, options?: { method?: string }) => {
      if (options?.method === 'POST' && path.includes('/extend')) {
        return { ...testShare, expiresAtUtc: '2026-11-13T00:00:00Z' }
      }
      return path.includes('download-attempts') ? { items: [], totalCount: 0, page: 1, pageSize: 25 } : [testShare]
    })

    renderSharesPage()

    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Süreyi uzat' })).toBeInTheDocument()
    })

    fireEvent.click(screen.getByRole('button', { name: 'Süreyi uzat' }))

    await waitFor(() => {
      expect(screen.getByText('Paylaşım süresini uzat')).toBeInTheDocument()
    })

    const select = screen.getByRole('combobox', { name: 'Ek süre' })
    // testShare used 24 of its 72 hours: 1 and 24 fit, 3 gün does not
    expect(within(select).getAllByRole('option').map((option) => option.textContent)).toEqual(['1 saat', '24 saat'])
    fireEvent.change(select, { target: { value: '24' } })
    fireEvent.submit(select.closest('form')!)

    await waitFor(() => {
      expect(request).toHaveBeenCalledWith(
        'tenants/tenant-a/backup-shares/share-1/extend',
        expect.objectContaining({
          method: 'POST',
          json: { additionalHours: 24 },
        })
      )
    })
  })

  it.each([
    ['72 hours used', '2026-11-14T12:00:00Z', 0],
    ['71 hours used', '2026-11-14T11:00:00Z', 1],
    ['48 hours used', '2026-11-13T12:00:00Z', 2],
    ['24 hours used', '2026-11-12T12:00:00Z', 2],
    ['1 hour used', '2026-11-11T13:00:00Z', 2],
  ])('offers only extensions that stay within 72 hours (%s)', async (_name, expiresAtUtc, optionCount) => {
    request.mockImplementation(async (path: string) => (path.includes('download-attempts') ? { items: [], totalCount: 0, page: 1, pageSize: 25 } : [{ ...testShare, expiresAtUtc }]))
    renderSharesPage()
    await screen.findByText('Audit inspection')
    if (optionCount === 0) {
      expect(screen.queryByRole('button', { name: 'Süreyi uzat' })).not.toBeInTheDocument()
      return
    }
    fireEvent.click(await screen.findByRole('button', { name: 'Süreyi uzat' }))
    expect(within(await screen.findByRole('combobox', { name: 'Ek süre' })).getAllByRole('option')).toHaveLength(optionCount)
  })

  it('explains the tab in plain Turkish and keeps download attempts in a closed section', async () => {
    request.mockImplementation(async (path: string) => (path.includes('download-attempts') ? { items: [], totalCount: 0, page: 1, pageSize: 25 } : [testShare]))
    renderSharesPage()
    expect(await screen.findByText("Platform Yöneticisi'nin bir yedeği süreli indirmesine verdiğiniz izinler. Genellikle destek isterken gerekir.")).toBeInTheDocument()
    expect(screen.getByText('İndirme girişimleri', { selector: 'summary' }).closest('details')).not.toHaveAttribute('open')
  })

  it('revokes active share from Paylaşımlarım', async () => {
    request.mockImplementation(async (path: string, options?: { method?: string }) => {
      if (options?.method === 'POST' && path.includes('/revoke')) {
        return { ...testShare, status: 'Revoked', revokedAtUtc: '2026-11-11T13:00:00Z' }
      }
      return path.includes('download-attempts') ? { items: [], totalCount: 0, page: 1, pageSize: 25 } : [testShare]
    })

    renderSharesPage()

    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'İptal et' })).toBeInTheDocument()
    })

    fireEvent.click(screen.getByRole('button', { name: 'İptal et' }))

    await waitFor(() => {
      expect(screen.getByRole('heading', { name: 'Paylaşım iptal edilsin mi?' })).toBeInTheDocument()
    })

    // Submit confirm form in dialog
    const confirmButtons = screen.getAllByRole('button', { name: 'İptal et' })
    const dialogConfirmBtn = confirmButtons[confirmButtons.length - 1]
    fireEvent.click(dialogConfirmBtn)

    await waitFor(() => {
      expect(request).toHaveBeenCalledWith(
        'tenants/tenant-a/backup-shares/share-1/revoke',
        expect.objectContaining({
          method: 'POST',
          json: {},
        })
      )
    })
  })

  it('UserAdmin cannot see extend/revoke actions on PlatformAdmin-created share, expired share, or revoked share', async () => {
    permissions.role = 'UserAdmin'
    request.mockImplementation(async (path: string) => (path.includes('download-attempts') ? { items: [], totalCount: 0, page: 1, pageSize: 25 } : [testShare, platformAdminShare, expiredShare, revokedShare]))

    renderSharesPage()

    await waitFor(() => {
      // Only 1 extend button and 1 revoke button for testShare
      expect(screen.getAllByRole('button', { name: 'Süreyi uzat' })).toHaveLength(1)
      expect(screen.getAllByRole('button', { name: 'İptal et' })).toHaveLength(1)
    })
  })

  it('PlatformAdmin can see extend/revoke actions on both PlatformAdmin-created and UserAdmin-created shares', async () => {
    permissions.role = 'PlatformAdmin'
    request.mockImplementation(async (path: string) => (path.includes('download-attempts') ? { items: [], totalCount: 0, page: 1, pageSize: 25 } : [testShare, platformAdminShare, expiredShare, revokedShare]))

    renderSharesPage()

    await waitFor(() => {
      // 2 active shares (testShare and platformAdminShare) both have buttons
      expect(screen.getAllByRole('button', { name: 'Süreyi uzat' })).toHaveLength(2)
      expect(screen.getAllByRole('button', { name: 'İptal et' })).toHaveLength(2)
    })
  })

  it('handles refusal on revoke gracefully by displaying error', async () => {
    request.mockImplementation(async (path: string, options?: { method?: string }) => {
      if (options?.method === 'POST' && path.includes('/revoke')) {
        throw new ApiError(403, { error: 'forbidden' })
      }
      return path.includes('download-attempts') ? { items: [], totalCount: 0, page: 1, pageSize: 25 } : [testShare]
    })

    renderSharesPage()

    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'İptal et' })).toBeInTheDocument()
    })

    fireEvent.click(screen.getByRole('button', { name: 'İptal et' }))

    await waitFor(() => {
      expect(screen.getByRole('heading', { name: 'Paylaşım iptal edilsin mi?' })).toBeInTheDocument()
    })

    const confirmButtons = screen.getAllByRole('button', { name: 'İptal et' })
    fireEvent.click(confirmButtons[confirmButtons.length - 1])

    await waitFor(() => {
      expect(screen.getByRole('alert')).toBeInTheDocument()
    })
  })

  it('renders download button on Paylaşılan Yedekler for active shares and triggers download', async () => {
    permissions.role = 'PlatformAdmin'
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {})
    request.mockImplementation(async (path: string, options?: { method?: string }) => {
      if (options?.method === 'POST' && path.includes('/downloads')) {
        return {
          sessionId: 'session-123',
          downloadUrl: '/api/tenants/tenant-b/backup-artifacts/art-123/downloads/session-123/content',
          filename: 'backup-123.bak',
          checksum: 'abc123',
          sizeBytes: 4096,
          decrypted: true,
        }
      }
      if (path.includes('platform/backup-shares')) return [platformShare, expiredShare]
      if (path.includes('platform/tenants')) return { items: [], totalCount: 0, pageSize: 100, page: 1 }
      return path.includes('download-attempts') ? { items: [], totalCount: 0, page: 1, pageSize: 25 } : []
    })

    renderPlatformSharesPage()

    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'İndir' })).toBeInTheDocument()
    })

    fireEvent.click(screen.getByRole('button', { name: 'İndir' }))

    await waitFor(() => {
      expect(click).toHaveBeenCalled()
      expect(screen.getByText('İndirme başlatıldı. Dosya tarayıcınız tarafından kaydediliyor.')).toBeInTheDocument()
    })
    click.mockRestore()
  })

  it('displays refusal states when download is refused or fails on Paylaşılan Yedekler', async () => {
    permissions.role = 'PlatformAdmin'
    request.mockImplementation(async (path: string, options?: { method?: string }) => {
      if (options?.method === 'POST' && path.includes('/downloads')) {
        throw new ApiError(403, { error: 'share_expired' })
      }
      if (path.includes('platform/backup-shares')) return [platformShare]
      if (path.includes('platform/tenants')) return { items: [], totalCount: 0, pageSize: 100, page: 1 }
      return path.includes('download-attempts') ? { items: [], totalCount: 0, page: 1, pageSize: 25 } : []
    })

    renderPlatformSharesPage()

    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'İndir' })).toBeInTheDocument()
    })

    fireEvent.click(screen.getByRole('button', { name: 'İndir' }))

    await waitFor(() => {
      expect(screen.getByRole('alert')).toHaveTextContent('İndirme yetkisi reddedildi veya paylaşım süresi doldu.')
    })
  })

  it('displays replica unavailable refusal when replica is not downloadable', async () => {
    permissions.role = 'PlatformAdmin'
    request.mockImplementation(async (path: string, options?: { method?: string }) => {
      if (options?.method === 'POST' && path.includes('/downloads')) {
        throw new ApiError(409, { error: 'replica_unavailable' })
      }
      if (path.includes('platform/backup-shares')) return [platformShare]
      if (path.includes('platform/tenants')) return { items: [], totalCount: 0, pageSize: 100, page: 1 }
      return path.includes('download-attempts') ? { items: [], totalCount: 0, page: 1, pageSize: 25 } : []
    })

    renderPlatformSharesPage()

    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'İndir' })).toBeInTheDocument()
    })

    fireEvent.click(screen.getByRole('button', { name: 'İndir' }))

    await waitFor(() => {
      expect(screen.getByRole('alert')).toHaveTextContent('Kopya şu anda indirme için kullanılamıyor.')
    })
  })

  it('refreshes an open download history after a refused download', async () => {
    permissions.role = 'PlatformAdmin'
    let attempted = false
    request.mockImplementation(async (path: string, options?: { method?: string }) => {
      if (options?.method === 'POST' && path.includes('/downloads')) {
        attempted = true
        throw new ApiError(409, { error: 'replica_unavailable' })
      }
      if (path.includes('platform/backup-shares')) return [platformShare]
      if (path.includes('platform/download-attempts')) return attemptPage(attempted ? [refusedAttempt] : [])
      if (path.includes('platform/tenants')) return { items: [], totalCount: 0, pageSize: 100, page: 1 }
      return []
    })
    renderPlatformSharesPage()
    fireEvent.click(screen.getByText('İndirme girişimleri', { selector: 'summary' }))
    await screen.findByText('Henüz indirme girişimi kaydı yok.')
    fireEvent.click(await screen.findByRole('button', { name: 'İndir' }))
    expect(await screen.findByText('Reddedildi')).toBeInTheDocument()
  })

  it('opens tenant download history when its sort is in the URL', async () => {
    request.mockImplementation(async (path: string) => path.includes('download-attempts') ? attemptPage([completedAttempt]) : [testShare])
    renderSharesPage('/tenants/tenant-a/backup-shares?attempts-sort=result')
    expect(await screen.findByRole('table', { name: 'İndirme girişimleri' })).toBeVisible()
    expect(screen.getByText('İndirme girişimleri', { selector: 'summary' }).closest('details')).toHaveAttribute('open')
  })

  it('renders tenant download attempt history in Paylaşımlarım for UserAdmin without platform retention controls', async () => {
    permissions.role = 'UserAdmin'
    request.mockImplementation(async (path: string) => {
      if (path.includes('download-attempts')) return path.includes('page=2')
        ? attemptPage([failedAttempt], 30)
        : attemptPage([completedAttempt, refusedAttempt], 30)
      if (path.includes('backup-shares')) return [testShare, revokedShare]
      return []
    })

    renderSharesPage()
    expect(screen.getByText('İndirme girişimleri', { selector: 'summary' }).closest('details')).not.toHaveAttribute('open')
    fireEvent.click(screen.getByText('İndirme girişimleri', { selector: 'summary' }))

    await waitFor(() => {
      expect(screen.getByRole('table', { name: 'Paylaşımlarım' })).toBeInTheDocument()
      expect(screen.getByText('İndirme girişimleri', { selector: 'summary' }).closest('details')).toHaveAttribute('open')
      const history = within(screen.getByRole('table', { name: 'İndirme girişimleri' }))
      expect(history.getByText('Tamamlandı')).toBeInTheDocument()
      expect(history.getByText('Reddedildi')).toBeInTheDocument()
      expect(screen.getByText('Paylaşım iptal edilmiş.')).toBeInTheDocument()
      expect(screen.queryByRole('heading', { name: 'Denetim ve paylaşım geçmişi saklama süresi' })).not.toBeInTheDocument()
    })
    expect(screen.queryByText('share_revoked')).not.toBeInTheDocument()
    const refusal = screen.getByText('Paylaşım iptal edilmiş.').closest<HTMLElement>('[role="alert"]')!
    fireEvent.click(within(refusal).getByRole('button', { name: 'Teknik ayrıntılar' }))
    const details = within(within(refusal).getByRole('region', { name: 'Teknik ayrıntılar' }))
    expect(details.getByText('share_revoked')).toBeVisible()
    expect(details.getByRole('button', { name: 'Kopyala' })).toBeInTheDocument()
    expect(attemptCalls()[0]).toBe('tenants/tenant-a/backup-shares/download-attempts?page=1&pageSize=25')

    // Pages come from the server; the pager replaces the old "show older" button.
    expect(screen.queryByText(/Dosya sağlama toplamı doğrulaması başarısız/)).not.toBeInTheDocument()
    fireEvent.click(within(screen.getByRole('navigation', { name: 'İndirme girişimi sayfaları' })).getByRole('button', { name: 'Sonraki' }))
    await waitFor(() => expect(screen.getByText(/Dosya sağlama toplamı doğrulaması başarısız/)).toBeInTheDocument())
    expect(attemptCalls().at(-1)).toContain('page=2&pageSize=25')
    expect(within(screen.getByRole('table', { name: 'İndirme girişimleri' })).queryByText('Tamamlandı')).not.toBeInTheDocument()
  })

  it('searches, sorts and filters the download history on the server, apart from the shares list', async () => {
    permissions.role = 'UserAdmin'
    request.mockImplementation(async (path: string) => {
      if (path.includes('download-attempts')) return attemptPage([completedAttempt])
      return [testShare]
    })
    renderSharesPage()
    fireEvent.click(screen.getByText('İndirme girişimleri', { selector: 'summary' }))
    const history = await screen.findByRole('region', { name: 'İndirme girişimi filtreleri' })

    fireEvent.change(within(history).getByRole('searchbox', { name: 'İndirme girişimi ara' }), { target: { value: 'ayşe' } })
    await waitFor(() => expect(attemptCalls().at(-1)).toBe('tenants/tenant-a/backup-shares/download-attempts?page=1&pageSize=25&search=ay%C5%9Fe'))
    expect(screen.getByRole('searchbox', { name: 'Paylaşım ara' })).toHaveValue('')
    expect(screen.getByTestId('location-search')).toHaveTextContent('attempts-search=ay%C5%9Fe')

    fireEvent.change(within(history).getByRole('combobox', { name: 'Sonuç' }), { target: { value: 'Refused' } })
    await waitFor(() => expect(attemptCalls().at(-1)).toContain('search=ay%C5%9Fe&result=Refused'))
    expect(within(history).queryByRole('textbox', { name: 'Paylaşım ID' })).not.toBeInTheDocument()

    // The share id means nothing to a user, so its column does not sort.
    expect(within(screen.getByRole('table', { name: 'İndirme girişimleri' })).queryByRole('button', { name: 'Paylaşım' })).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Sonuç' }))
    await waitFor(() => expect(attemptCalls().at(-1)).toContain('sort=result&dir=asc'))
    fireEvent.click(screen.getByRole('button', { name: 'Sonuç' }))
    await waitFor(() => expect(attemptCalls().at(-1)).toContain('sort=result&dir=desc'))
  })

  it('says so when nothing matches the search, and offers to clear it', async () => {
    permissions.role = 'UserAdmin'
    request.mockImplementation(async (path: string) => path.includes('download-attempts') ? attemptPage([]) : [testShare])
    renderSharesPage('/tenants/tenant-a/backup-shares?attempts-search=zzz')
    fireEvent.click(screen.getByText('İndirme girişimleri', { selector: 'summary' }))
    expect(await screen.findByText('Aramanızla eşleşen indirme girişimi yok.')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Filtreleri temizle' }))
    expect(await screen.findByText('Henüz indirme girişimi kaydı yok.')).toBeInTheDocument()
  })

  it('opens filtered platform audit history from a deep link', async () => {
    permissions.role = 'PlatformAdmin'
    request.mockImplementation(async (path: string) => {
      if (path.includes('platform/download-attempts')) return attemptPage([failedAttempt])
      if (path.includes('audit-retention')) return { retentionDays: 365 }
      return []
    })
    renderPlatformSharesPage('/platform/shares?result=Failed')
    expect(await screen.findByRole('combobox', { name: 'Sonuç' })).toHaveValue('Failed')
    expect(await screen.findByText(/Dosya sağlama toplamı doğrulaması başarısız/)).toBeVisible()
  })

  it('opens the platform download history when its search is in the URL', async () => {
    permissions.role = 'PlatformAdmin'
    request.mockImplementation(async (path: string) => {
      if (path.includes('platform/download-attempts')) return attemptPage([failedAttempt])
      if (path.includes('audit-retention')) return { retentionDays: 365 }
      return []
    })
    renderPlatformSharesPage('/platform/shares?attempts-search=sağlama')
    expect(await screen.findByRole('searchbox', { name: 'İndirme girişimi ara' })).toHaveValue('sağlama')
    expect(screen.getByText('İndirme girişimleri', { selector: 'summary' }).closest('details')).toHaveAttribute('open')
  })

  it('renders download attempt history with filters and configurable 365-day retention in Paylaşılan Yedekler for PlatformAdmin', async () => {
    permissions.role = 'PlatformAdmin'
    request.mockImplementation(async (path: string, options?: { method?: string; json?: unknown }) => {
      if (path.includes('platform/settings/audit-retention')) {
        if (options?.method === 'PUT') {
          return {
            retentionDays: 180,
            defaultRetentionDays: 365,
            minRetentionDays: 1,
            maxRetentionDays: 36500,
            updatedAtUtc: '2026-11-12T10:00:00Z',
          }
        }
        return {
          retentionDays: 365,
          defaultRetentionDays: 365,
          minRetentionDays: 1,
          maxRetentionDays: 36500,
          updatedAtUtc: null,
        }
      }
      if (path.includes('platform/download-attempts')) {
        if (path.includes('result=Failed')) return attemptPage([failedAttempt])
        return attemptPage([completedAttempt, refusedAttempt, failedAttempt])
      }
      if (path.includes('platform/backup-shares')) return [platformShare]
      if (path.includes('platform/tenants')) return { items: [], totalCount: 0, pageSize: 100, page: 1 }
      return path.includes('download-attempts') ? { items: [], totalCount: 0, page: 1, pageSize: 25 } : []
    })

    renderPlatformSharesPage()
    expect(screen.getByText('İndirme girişimleri', { selector: 'summary' }).closest('details')).not.toHaveAttribute('open')
    fireEvent.click(screen.getByText('İndirme girişimleri', { selector: 'summary' }))
    fireEvent.click(screen.getByText('Denetim ve paylaşım geçmişi saklama süresi', { selector: 'summary' }))

    await waitFor(() => {
      expect(screen.getByText('İndirme girişimleri', { selector: 'summary' }).closest('details')).toHaveAttribute('open')
      expect(screen.getByText('Paylaşım iptal edilmiş.')).toBeInTheDocument()
      expect(screen.getByText(/Dosya sağlama toplamı doğrulaması başarısız/)).toBeInTheDocument()
      expect(screen.getByDisplayValue('365')).toBeInTheDocument()
    })

    // Filter by result = Failed
    const resultSelect = screen.getByRole('combobox', { name: 'Sonuç' })
    fireEvent.change(resultSelect, { target: { value: 'Failed' } })

    await waitFor(() => {
      expect(request).toHaveBeenCalledWith(
        expect.stringContaining('result=Failed'),
        expect.anything()
      )
    })

    // Update retention period to 180 days
    const retentionInput = screen.getByLabelText('Saklama süresi (gün)')
    fireEvent.change(retentionInput, { target: { value: '180' } })
    fireEvent.click(screen.getByRole('button', { name: 'Saklama süresini kaydet' }))

    await waitFor(() => {
      expect(request).toHaveBeenCalledWith(
        'platform/settings/audit-retention',
        expect.objectContaining({
          method: 'PUT',
          json: { retentionDays: 180 },
        })
      )
      expect(screen.getByText('Saklama süresi güncellendi.')).toBeInTheDocument()
    })
  })
})
