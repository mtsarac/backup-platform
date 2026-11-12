import { cleanup, fireEvent, screen, waitFor } from '@testing-library/react'
import { render } from '@/test/render'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ArtifactDetailPage } from '@/pages/artifacts/artifact-detail-page'
import { SharesPage } from '@/pages/shares/shares-page'
import { PlatformSharesPage } from '@/pages/platform/platform-shares-page'
import { ApiError, apiRequest } from '@/lib/api/client'
import type * as ApiClient from '@/lib/api/client'
import type { BackupArtifact } from '@/features/artifacts/types'
import type { BackupShareView, DownloadAttemptView } from './types'

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

const permissions = vi.hoisted(() => ({ canAdmin: true, canOperate: true, role: 'UserAdmin' }))
vi.mock('@/features/tenancy/use-tenant', () => ({
  useTenant: () => ({ tenantId: 'tenant-a', canAdmin: permissions.canAdmin, canOperate: permissions.canOperate }),
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
  permissions.canAdmin = true
  permissions.canOperate = true
  permissions.role = 'UserAdmin'
})

function renderDetailPage() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  })
  render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/tenants/tenant-a/artifacts/artifact-1']}>
        <Routes>
          <Route path="/tenants/:tenantId/artifacts/:id" element={<ArtifactDetailPage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>
  )
  return queryClient
}

function renderSharesPage() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  })
  render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/tenants/tenant-a/shares']}>
        <Routes>
          <Route path="/tenants/:tenantId/shares" element={<SharesPage />} />
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
      return []
    })

    renderDetailPage()

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
      return []
    })

    renderDetailPage()

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

  it('validates duration between 1 and 72 hours', async () => {
    request.mockImplementation(async (path: string) => {
      if (path.includes('backup-shares') || path.includes('/shares')) return []
      if (path.includes('repairs')) return []
      if (path.endsWith('backup-artifacts/artifact-1')) return testArtifact
      return []
    })

    renderDetailPage()

    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Paylaşım oluştur' })).toBeInTheDocument()
    })

    fireEvent.click(screen.getByRole('button', { name: 'Paylaşım oluştur' }))

    await waitFor(() => {
      expect(screen.getByText('Yeni paylaşım oluştur')).toBeInTheDocument()
    })

    const durationInput = screen.getByDisplayValue('24')
    fireEvent.change(durationInput, { target: { value: '80' } })

    const form = durationInput.closest('form')!
    fireEvent.submit(form)

    await waitFor(() => {
      expect(screen.getByText('Süre 1 ile 72 saat arasında olmalıdır.')).toBeInTheDocument()
    })
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
      return []
    })

    renderDetailPage()

    await waitFor(() => {
      const button = screen.getByRole('button', { name: 'Paylaşım oluştur' })
      expect(button).toBeDisabled()
      expect(
        screen.getByText('İndirilebilir doğrulanmış bir kopya bulunmadığından paylaşım oluşturulamaz.')
      ).toBeInTheDocument()
    })
  })

  it('renders Paylaşımlarım page with active, expired, and revoked shares', async () => {
    request.mockImplementation(async () => [testShare, expiredShare, revokedShare])

    renderSharesPage()

    await waitFor(() => {
      expect(screen.getByRole('heading', { name: 'Paylaşımlarım' })).toBeInTheDocument()
      expect(screen.getAllByText('ProductionDb')).toHaveLength(3)
      expect(screen.getByText('Aktif')).toBeInTheDocument()
      expect(screen.getByText('Süresi doldu')).toBeInTheDocument()
      expect(screen.getByText('İptal edildi')).toBeInTheDocument()
    })
  })

  it('renders empty state in Paylaşımlarım when no shares exist', async () => {
    request.mockImplementation(async () => [])

    renderSharesPage()

    await waitFor(() => {
      expect(screen.getByText('Henüz paylaşım yapılmamış.')).toBeInTheDocument()
      expect(
        screen.getByText('Yedek dosyası detayından PlatformAdmin için paylaşım oluşturabilirsiniz.')
      ).toBeInTheDocument()
    })
  })

  it('shows PlatformAdmin-created shares in Paylaşımlarım as read-only with PlatformAdmin role', async () => {
    request.mockImplementation(async () => [platformShare])

    renderSharesPage()

    await waitFor(() => {
      expect(screen.getByText('AnalyticsDb')).toBeInTheDocument()
      expect(screen.getByText('Platform incident investigation')).toBeInTheDocument()
      expect(screen.getByText('Super Admin')).toBeInTheDocument()
      expect(screen.getByText('PlatformAdmin')).toBeInTheDocument()
      expect(screen.getByText('Aktif')).toBeInTheDocument()
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
      return []
    })

    renderPlatformSharesPage()

    await waitFor(() => {
      expect(screen.getByRole('heading', { name: 'Paylaşılan Yedekler' })).toBeInTheDocument()
      expect(screen.getByText('Acme Corp')).toBeInTheDocument()
      expect(screen.getByText('Platform incident investigation')).toBeInTheDocument()
      expect(screen.getByText('Super Admin')).toBeInTheDocument()
      expect(screen.getByText('PlatformAdmin')).toBeInTheDocument()
      expect(screen.getByRole('region', { name: 'Paylaşım filtreleri' })).toBeInTheDocument()
      expect(screen.getByRole('button', { name: 'Paylaşım oluştur' })).toBeInTheDocument()
    })
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
      return []
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
    const tenantInput = screen.getByRole('combobox', { name: 'Çalışma Alanı' })
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
      return [testShare]
    })

    renderSharesPage()

    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Süreyi uzat' })).toBeInTheDocument()
    })

    fireEvent.click(screen.getByRole('button', { name: 'Süreyi uzat' }))

    await waitFor(() => {
      expect(screen.getByText('Paylaşım süresini uzat')).toBeInTheDocument()
    })

    const input = screen.getByDisplayValue('24')
    fireEvent.change(input, { target: { value: '12' } })

    const form = input.closest('form')!
    fireEvent.submit(form)

    await waitFor(() => {
      expect(request).toHaveBeenCalledWith(
        'tenants/tenant-a/backup-shares/share-1/extend',
        expect.objectContaining({
          method: 'POST',
          json: { additionalHours: 12 },
        })
      )
    })
  })

  it('revokes active share from Paylaşımlarım', async () => {
    request.mockImplementation(async (path: string, options?: { method?: string }) => {
      if (options?.method === 'POST' && path.includes('/revoke')) {
        return { ...testShare, status: 'Revoked', revokedAtUtc: '2026-11-11T13:00:00Z' }
      }
      return [testShare]
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
    permissions.canAdmin = true
    request.mockImplementation(async () => [testShare, platformAdminShare, expiredShare, revokedShare])

    renderSharesPage()

    await waitFor(() => {
      // Only 1 extend button and 1 revoke button for testShare
      expect(screen.getAllByRole('button', { name: 'Süreyi uzat' })).toHaveLength(1)
      expect(screen.getAllByRole('button', { name: 'İptal et' })).toHaveLength(1)
    })
  })

  it('PlatformAdmin can see extend/revoke actions on both PlatformAdmin-created and UserAdmin-created shares', async () => {
    permissions.role = 'PlatformAdmin'
    permissions.canAdmin = false
    request.mockImplementation(async () => [testShare, platformAdminShare, expiredShare, revokedShare])

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
      return [testShare]
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
      return []
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
      return []
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
      return []
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

  it('renders tenant download attempt history in Paylaşımlarım for UserAdmin without platform retention controls', async () => {
    permissions.role = 'UserAdmin'
    permissions.canAdmin = true
    request.mockImplementation(async (path: string) => {
      if (path.includes('download-attempts')) return [completedAttempt, refusedAttempt]
      if (path.includes('backup-shares')) return [testShare, revokedShare]
      return []
    })

    renderSharesPage()

    await waitFor(() => {
      expect(screen.getByRole('heading', { name: 'Paylaşımlarım' })).toBeInTheDocument()
      expect(screen.getByRole('heading', { name: 'İndirme girişimleri' })).toBeInTheDocument()
      expect(screen.getByText('Tamamlandı')).toBeInTheDocument()
      expect(screen.getByText('Reddedildi')).toBeInTheDocument()
      expect(screen.getByText('share_revoked')).toBeInTheDocument()
      expect(screen.queryByRole('heading', { name: 'Denetim ve paylaşım geçmişi saklama süresi' })).not.toBeInTheDocument()
    })
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
        if (path.includes('result=Failed')) return [failedAttempt]
        return [completedAttempt, refusedAttempt, failedAttempt]
      }
      if (path.includes('platform/backup-shares')) return [platformShare]
      if (path.includes('platform/tenants')) return { items: [], totalCount: 0, pageSize: 100, page: 1 }
      return []
    })

    renderPlatformSharesPage()

    await waitFor(() => {
      expect(screen.getByRole('heading', { name: 'İndirme girişimleri' })).toBeInTheDocument()
      expect(screen.getByText('share_revoked')).toBeInTheDocument()
      expect(screen.getByText('integrity_mismatch')).toBeInTheDocument()
      expect(screen.getByDisplayValue('365')).toBeInTheDocument()
    })

    // Filter by result = Failed
    const resultSelect = screen.getByRole('combobox', { name: 'Sonuç' })
    fireEvent.change(resultSelect, { target: { value: 'Failed' } })

    await waitFor(() => {
      expect(request).toHaveBeenCalledWith(
        expect.stringContaining('platform/download-attempts?result=Failed'),
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
