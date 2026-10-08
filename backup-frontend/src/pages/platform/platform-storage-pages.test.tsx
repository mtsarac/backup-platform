import { beforeEach, describe, expect, it, vi } from 'vitest'
import { fireEvent, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import * as storageApi from '@/features/platform/storage/api'
import { paged, renderPlatform } from '@/features/platform/test-utils'
import type { QuotaPolicy, StorageUsageRow, UsageReconciliation } from '@/features/storage/types'
import { ApiError } from '@/lib/api/client'
import { PlatformStoragePage } from './platform-storage-page'
import { PlatformQuotaPoliciesPage } from './platform-quota-policies-page'

vi.mock('@/features/platform/storage/api')

const samplePolicy: QuotaPolicy = {
  policyId: 'policy-1',
  key: 'default',
  name: 'Default Policy',
  version: 1,
  accountingMode: 'LogicalArtifactBytes',
  includedStorageScopes: [{ kind: 'all' }],
  enforcementMode: 'BlockNewBackups',
  warningThresholdPercent: 80,
  criticalThresholdPercent: 95,
  enabled: true,
  isDefault: true,
  createdAt: '2026-10-01T00:00:00Z',
  updatedAt: '2026-10-01T00:00:00Z',
}

const sampleUsageRow: StorageUsageRow = {
  tenantId: 'tenant-1',
  tenantName: 'Acme Corp',
  plan: { name: 'Standard 10 GB', isSystem: false, systemKey: null, subscriptionEndsAt: null },
  subscriptionStatus: 'Active',
  limitBytes: 10_000_000_000,
  unlimited: false,
  usedBytes: 5_000_000_000,
  reservedBytes: 1_000_000_000,
  remainingBytes: 4_000_000_000,
  state: 'Normal',
  warningThreshold: 80,
  criticalThreshold: 95,
  accountingMode: 'LogicalArtifactBytes',
  enforcementMode: 'BlockNewBackups',
  newBackupsAllowed: true,
  denialCode: null,
  policy: { policyId: 'policy-1', key: 'default', version: 1 },
  hasUsage: true,
  usageCurrent: true,
  calculatedAt: '2026-10-08T12:00:00Z',
}

beforeEach(() => {
  vi.resetAllMocks()
  vi.mocked(storageApi.getStorageUsage).mockResolvedValue(paged([sampleUsageRow]))
  vi.mocked(storageApi.getQuotaPolicies).mockResolvedValue({ items: [samplePolicy] })
})

describe('Platform Storage Usage Page states and operations', () => {
  it('renders loading, then storage rows', async () => {
    vi.mocked(storageApi.getStorageUsage).mockReturnValue(new Promise(() => undefined))
    renderPlatform(<PlatformStoragePage />, { path: '/platform/storage', route: '/platform/storage' })
    expect(await screen.findByRole('status')).toHaveTextContent('Yükleniyor')
  })

  it('renders error state when 500 fails and 403 authorization alert', async () => {
    vi.mocked(storageApi.getStorageUsage).mockRejectedValue(new ApiError(500))
    const { unmount } = renderPlatform(<PlatformStoragePage />, { path: '/platform/storage', route: '/platform/storage' })
    expect(await screen.findByText('Sunucu isteği tamamlayamadı. Daha sonra tekrar deneyin.')).toBeInTheDocument()
    unmount()

    vi.mocked(storageApi.getStorageUsage).mockRejectedValue(new ApiError(403))
    renderPlatform(<PlatformStoragePage />, { path: '/platform/storage', route: '/platform/storage' })
    expect(await screen.findByRole('alert')).toHaveTextContent('Bu alan için yetkiniz yok')
  })

  it('opens confirmation dialog and runs Reconcile', async () => {
    const reconResult: UsageReconciliation = {
      tenantId: 'tenant-1',
      drifted: true,
      previousUsedBytes: 4_000_000_000,
      previousReservedBytes: 2_000_000_000,
      previousPolicyVersion: 1,
      usedBytes: 5_000_000_000,
      reservedBytes: 1_000_000_000,
      policyId: 'policy-1',
      policyVersion: 1,
      reservationsReleased: 1,
    }
    vi.mocked(storageApi.reconcileUsage).mockResolvedValue(reconResult)

    renderPlatform(<PlatformStoragePage />, { path: '/platform/storage', route: '/platform/storage' })
    expect(await screen.findByText('Acme Corp')).toBeInTheDocument()

    const reconcileBtn = screen.getByRole('button', { name: 'Reconcile' })
    fireEvent.click(reconcileBtn)

    expect(await screen.findByText('Depolama senkronizasyonu çalıştırılsın mı (Reconcile)?')).toBeInTheDocument()

    const confirmBtn = screen.getByRole('button', { name: 'Reconcile Çalıştır' })
    fireEvent.click(confirmBtn)

    await waitFor(() => {
      expect(storageApi.reconcileUsage).toHaveBeenCalledWith('tenant-1')
    })
    expect(await screen.findByText(/sapma düzeltildi/)).toBeInTheDocument()
  })

  it('opens confirmation dialog and runs Rebuild', async () => {
    const reconResult: UsageReconciliation = {
      tenantId: 'tenant-1',
      drifted: false,
      previousUsedBytes: null,
      previousReservedBytes: null,
      previousPolicyVersion: null,
      usedBytes: 5_000_000_000,
      reservedBytes: 0,
      policyId: 'policy-1',
      policyVersion: 1,
      reservationsReleased: 0,
    }
    vi.mocked(storageApi.rebuildUsage).mockResolvedValue(reconResult)

    renderPlatform(<PlatformStoragePage />, { path: '/platform/storage', route: '/platform/storage' })
    expect(await screen.findByText('Acme Corp')).toBeInTheDocument()

    const rebuildBtn = screen.getByRole('button', { name: 'Rebuild' })
    fireEvent.click(rebuildBtn)

    expect(await screen.findByText('Depolama baştan hesaplansın mı (Rebuild)?')).toBeInTheDocument()

    const confirmBtn = screen.getByRole('button', { name: 'Rebuild Çalıştır' })
    fireEvent.click(confirmBtn)

    await waitFor(() => {
      expect(storageApi.rebuildUsage).toHaveBeenCalledWith('tenant-1')
    })
    expect(await screen.findByText(/kullanım baştan hesaplandı/)).toBeInTheDocument()
  })
})

describe('Platform Quota Policies Page and Dialog', () => {
  it('renders policies list and handles create dialog with ProviderType scope', async () => {
    const user = userEvent.setup()
    vi.mocked(storageApi.createQuotaPolicy).mockResolvedValue({
      ...samplePolicy,
      policyId: 'new-policy',
      key: 'provider-policy',
      name: 'Provider Policy',
    })

    renderPlatform(<PlatformQuotaPoliciesPage />, { path: '/platform/quota-policies', route: '/platform/quota-policies' })
    expect(await screen.findByText('Default Policy')).toBeInTheDocument()

    const newBtn = screen.getByRole('button', { name: /Yeni Politika/i })
    await user.click(newBtn)

    expect(await screen.findByText('Yeni Kota Politikası')).toBeInTheDocument()

    await user.type(screen.getByLabelText(/Politika Anahtarı/i), 'provider-policy')
    await user.type(screen.getByLabelText(/Politika Adı/i), 'Provider Policy')

    // Change scope to ProviderType
    const scopeSelect = screen.getByLabelText(/Depolama Kapsamı/i)
    await user.selectOptions(scopeSelect, 'providerType')

    expect(screen.getAllByText(/Provider Anahtarları/i)[0]).toBeInTheDocument()

    const submitBtn = screen.getByRole('button', { name: 'Oluştur' })
    await user.click(submitBtn)

    await waitFor(() => {
      expect(storageApi.createQuotaPolicy).toHaveBeenCalledWith(
        expect.objectContaining({
          key: 'provider-policy',
          name: 'Provider Policy',
          includedStorageScopes: [{ kind: 'providerType', values: ['central'] }],
        }),
      )
    })
  })

  it('handles edit policy and updates OwnershipType scope', async () => {
    const user = userEvent.setup()
    vi.mocked(storageApi.updateQuotaPolicy).mockResolvedValue({
      policy: { ...samplePolicy, name: 'Updated Policy' },
      tenantsRecalculated: 2,
    })

    renderPlatform(<PlatformQuotaPoliciesPage />, { path: '/platform/quota-policies', route: '/platform/quota-policies' })
    expect(await screen.findByText('Default Policy')).toBeInTheDocument()

    const editBtn = screen.getByRole('button', { name: 'Düzenle' })
    await user.click(editBtn)

    expect(await screen.findByText(/Politikayı düzenle: Default Policy/i)).toBeInTheDocument()

    const scopeSelect = screen.getByLabelText(/Depolama Kapsamı/i)
    await user.selectOptions(scopeSelect, 'ownershipType')

    expect(screen.getByText(/Dahil edilecek sahiplik türleri/i)).toBeInTheDocument()

    const saveBtn = screen.getByRole('button', { name: 'Kaydet' })
    await user.click(saveBtn)

    await waitFor(() => {
      expect(storageApi.updateQuotaPolicy).toHaveBeenCalledWith(
        'policy-1',
        expect.objectContaining({
          name: 'Default Policy',
          includedStorageScopes: [{ kind: 'ownershipType', values: ['PlatformManaged'] }],
        }),
      )
    })
  })
})
