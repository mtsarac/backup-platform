import { cleanup, fireEvent, screen, waitFor } from '@testing-library/react'
import { render } from '@/test/render'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ArtifactDetailPage } from '@/pages/artifacts/artifact-detail-page'
import { SharesPage } from '@/pages/shares/shares-page'
import { apiRequest } from '@/lib/api/client'
import type { BackupArtifact } from '@/features/artifacts/types'
import type { BackupShareView } from './types'

vi.mock('@/lib/api/client', () => ({
  apiRequest: vi.fn(),
  API_BASE_URL: '/api',
  tenantPath: (tenantId: string, path: string) => `tenants/${tenantId}/${path}`,
}))

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
})
