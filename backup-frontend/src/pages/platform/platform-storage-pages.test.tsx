import { beforeEach, describe, expect, it, vi } from 'vitest'
import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import * as storageApi from '@/features/platform/storage/api'
import { paged, renderPlatform } from '@/features/platform/test-utils'
import type { QuotaPolicy, StorageUsageRow, UsageReconciliation } from '@/features/storage/types'
import { ApiError } from '@/lib/api/client'
import { PlatformStoragePage } from './platform-storage-page'
import { PlatformQuotaPoliciesPage } from './platform-quota-policies-page'
import { TenantStorageSection } from '@/features/platform/storage/tenant-storage-section'
import { setViewport } from '@/test/viewport'

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

  it('shows usage and standing first with copyable policy information collapsed', async () => {
    renderPlatform(<PlatformStoragePage />, { path: '/platform/storage', route: '/platform/storage' })
    await screen.findByText('Acme Corp')
    expect(screen.getByRole('progressbar')).toBeInTheDocument()
    const policy = await screen.findByText('policy-1')
    const details = policy.closest('details')
    expect(details).not.toHaveAttribute('open')
    await userEvent.click(within(details!).getByText('Teknik ayrıntılar'))
    expect(within(details!).getByRole('button', { name: /Kopyala:.*[Pp]olitika kimliği/ })).toBeInTheDocument()
    expect(within(details!).getByText('5000000000')).toBeInTheDocument()
    expect(within(details!).getByText('LogicalArtifactBytes')).toBeInTheDocument()
  })

  it('keeps usage, standing and policy disclosure available in mobile cards', async () => {
    setViewport(false)
    renderPlatform(<PlatformStoragePage />, { path: '/platform/storage', route: '/platform/storage' })
    await screen.findByText('Acme Corp')
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    const list = screen.getByRole('list', { name: 'Depolama kullanımı' })
    expect(within(list).getByRole('link', { name: 'Acme Corp' })).toBeInTheDocument()
    expect(within(list).getByRole('progressbar')).toBeInTheDocument()
    expect(within(list).getByText('policy-1').closest('details')).not.toHaveAttribute('open')
    expect(within(list).getByRole('button', { name: 'Baştan hesapla' })).toBeInTheDocument()
  })

  it('keeps missing usage neutral and offers clearing filters when no workspaces match', async () => {
    vi.mocked(storageApi.getStorageUsage).mockResolvedValue(paged([{ ...sampleUsageRow, hasUsage: false, usedBytes: null, reservedBytes: null, remainingBytes: null, state: null }]))
    const { unmount } = renderPlatform(<PlatformStoragePage />, { path: '/platform/storage', route: '/platform/storage' })
    await screen.findByText('Acme Corp')
    expect(screen.queryByRole('progressbar')).not.toBeInTheDocument()
    expect(screen.getAllByText('Henüz hesaplanmadı').length).toBeGreaterThan(0)
    unmount()
    vi.mocked(storageApi.getStorageUsage).mockResolvedValue(paged([]))
    renderPlatform(<PlatformStoragePage />, { path: '/platform/storage?search=missing&quotaState=Warning', route: '/platform/storage' })
    await screen.findByText('Kriterlere uygun çalışma alanı kullanımı bulunamadı.')
    await userEvent.click(screen.getAllByRole('button', { name: 'Filtreleri temizle' }).at(-1)!)
    await waitFor(() => expect(screen.getByTestId('location')).toHaveTextContent('/platform/storage'))
    expect(storageApi.getStorageUsage).toHaveBeenLastCalledWith(expect.objectContaining({ search: '', quotaState: undefined }), expect.any(AbortSignal))
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

    const reconcileBtn = screen.getByRole('button', { name: "Kullanımı doğrula" })
    fireEvent.click(reconcileBtn)

    expect(await screen.findByText("Depolama eşitlemesi çalıştırılsın mı?")).toBeInTheDocument()

    const confirmBtn = screen.getByRole('button', { name: "Uzlaştırmayı Çalıştır" })
    fireEvent.click(confirmBtn)

    await waitFor(() => {
      expect(storageApi.reconcileUsage).toHaveBeenCalledWith('tenant-1')
    })
    expect(await screen.findByText(/sapma düzeltildi/)).toBeInTheDocument()
  })

  it('requires the exact tenant name before rebuilding usage from stored backup records', async () => {
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

    const rebuildBtn = screen.getByRole('button', { name: "Baştan hesapla" })
    fireEvent.click(rebuildBtn)

    expect(await screen.findByText("Depolama baştan hesaplansın mı?")).toBeInTheDocument()

    const confirmBtn = screen.getByRole('button', { name: "Yeniden Hesaplamayı Çalıştır" })
    expect(confirmBtn).toBeDisabled()
    const confirmationName = screen.getByRole('textbox')
    await userEvent.type(confirmationName, 'acme corp')
    expect(confirmBtn).toBeDisabled()
    expect(storageApi.rebuildUsage).not.toHaveBeenCalled()
    await userEvent.clear(confirmationName)
    await userEvent.type(confirmationName, 'Acme Corp')
    fireEvent.click(confirmBtn)

    await waitFor(() => {
      expect(storageApi.rebuildUsage).toHaveBeenCalledWith('tenant-1')
    })
    expect(await screen.findByText(/kullanım baştan hesaplandı/)).toBeInTheDocument()
  })

  it('requires the tenant name for rebuild on tenant detail too', async () => {
    vi.mocked(storageApi.rebuildUsage).mockResolvedValue({
      tenantId: 'tenant-1', drifted: false, previousUsedBytes: 5_000_000_000, previousReservedBytes: 0,
      previousPolicyVersion: 1, usedBytes: 5_000_000_000, reservedBytes: 0, policyId: 'policy-1',
      policyVersion: 1, reservationsReleased: 0,
    })
    vi.mocked(storageApi.getPlatformTenantQuota).mockResolvedValue({
      ...sampleUsageRow, state: 'Normal', usedBytes: 5_000_000_000, reservedBytes: 1_000_000_000,
      includedStorageScopes: [{ kind: 'all' }], calculatedAt: '2026-10-08T12:00:00Z',
    })
    renderPlatform(<TenantStorageSection tenantId="tenant-1" tenantName="Acme Corp" />)
    await screen.findByText('Depolama ve Kota')
    await userEvent.click(screen.getByRole('button', { name: 'Baştan hesapla' }))
    const dialog = await screen.findByRole('dialog')
    const confirm = within(dialog).getByRole('button', { name: 'Sıfırdan hesapla' })
    expect(confirm).toBeDisabled()
    await userEvent.type(within(dialog).getByRole('textbox'), 'Acme Corp')
    expect(confirm).toBeEnabled()
    await userEvent.click(confirm)
    await waitFor(() => expect(storageApi.rebuildUsage).toHaveBeenCalledWith('tenant-1'))
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

    await user.type(screen.getByRole('textbox', { name: /Politika Anahtarı/i }), 'provider-policy')
    await user.type(screen.getByRole('textbox', { name: /Politika Adı/i }), 'Provider Policy')

    // Change scope to ProviderType
    const scopeSelect = screen.getByLabelText(/Depolama Kapsamı/i)
    await user.selectOptions(scopeSelect, 'providerType')

    expect(screen.getAllByText(/(?:Provider|Sağlayıcı) Anahtarları/i)[0]).toBeInTheDocument()

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

  it('confirms affected workspaces and old and new thresholds before updating policy scope', async () => {
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
    await user.clear(screen.getByRole('spinbutton', { name: /Uyarı Eşiği/i }))
    await user.type(screen.getByRole('spinbutton', { name: /Uyarı Eşiği/i }), '70')

    const saveBtn = screen.getByRole('button', { name: 'Kaydet' })
    await user.click(saveBtn)
    expect(storageApi.updateQuotaPolicy).not.toHaveBeenCalled()
    const confirmation = await screen.findByRole('dialog')
    expect(within(confirmation).queryByRole('textbox')).not.toBeInTheDocument()
    expect(confirmation).toHaveTextContent('Default Policy')
    expect(confirmation).toHaveTextContent('80')
    expect(confirmation).toHaveTextContent('70')
    expect(confirmation).toHaveTextContent('95')
    expect(confirmation).toHaveTextContent(/varsayılan/i)
    await user.click(within(confirmation).getByRole('button', { name: 'Kaydet' }))

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

  it('explains the custom policy cohort and default fallback, and lets cancellation preserve scope edits', async () => {
    const scopes = [{ kind: 'quotaScope' as const, values: ['managed'] }, { kind: 'providerType' as const, values: ['central'] }]
    vi.mocked(storageApi.getQuotaPolicies).mockResolvedValue({ items: [{ ...samplePolicy, name: 'Custom policy', isDefault: false, includedStorageScopes: scopes }] })
    vi.mocked(storageApi.updateQuotaPolicy).mockResolvedValue({ policy: samplePolicy, tenantsRecalculated: 1 })
    renderPlatform(<PlatformQuotaPoliciesPage />, { path: '/platform/quota-policies', route: '/platform/quota-policies' })
    await screen.findByText('Custom policy')
    await userEvent.click(screen.getByRole('button', { name: 'Düzenle' }))
    await userEvent.click(screen.getByRole('checkbox', { name: /Etkin politika/ }))
    await userEvent.click(screen.getByRole('button', { name: 'Kaydet' }))
    let dialog = await screen.findByRole('dialog')
    expect(dialog).toHaveTextContent(/Planında “Custom policy”/)
    expect(dialog).toHaveTextContent(/varsayılan politikaya geçer/)
    await userEvent.click(within(dialog).getByRole('button', { name: 'Vazgeç' }))
    expect(storageApi.updateQuotaPolicy).not.toHaveBeenCalled()
    expect(screen.getByLabelText(/Kota kapsamı etiketleri/)).toHaveValue('managed')
    expect(screen.getByRole('checkbox', { name: /Etkin politika/ })).not.toBeChecked()
    await userEvent.click(screen.getByRole('button', { name: 'Kaydet' }))
    dialog = await screen.findByRole('dialog')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Kaydet' }))
    await waitFor(() => expect(storageApi.updateQuotaPolicy).toHaveBeenCalledWith('policy-1', expect.objectContaining({ enabled: false, includedStorageScopes: scopes })))
  })

  it('creates a policy with a quotaScope selector and validates labels', async () => {
    const user = userEvent.setup()
    vi.mocked(storageApi.createQuotaPolicy).mockResolvedValue({ ...samplePolicy, policyId: 'qs', key: 'managed-only', name: 'Managed only' })
    renderPlatform(<PlatformQuotaPoliciesPage />, { path: '/platform/quota-policies', route: '/platform/quota-policies' })
    await screen.findByText('Default Policy')
    await user.click(screen.getByRole('button', { name: /Yeni Politika/i }))
    await user.type(await screen.findByRole('textbox', { name: /Politika Anahtarı/i }), 'managed-only')
    await user.type(screen.getByRole('textbox', { name: /Politika Adı/i }), 'Managed only')
    await user.selectOptions(screen.getByLabelText(/Depolama Kapsamı/i), 'quotaScope')
    const labels = screen.getByLabelText(/Kota kapsamı etiketleri/)
    expect(labels).toHaveValue('managed')

    await user.clear(labels)
    await user.type(labels, 'Managed, tenant')
    await user.click(screen.getByRole('button', { name: 'Oluştur' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('“Managed” geçerli bir kota kapsamı etiketi değil')
    expect(storageApi.createQuotaPolicy).not.toHaveBeenCalled()

    await user.clear(labels)
    await user.type(labels, 'x'.repeat(65))
    await user.click(screen.getByRole('button', { name: 'Oluştur' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('en fazla 64 karakter')

    await user.clear(labels)
    await user.click(screen.getByRole('button', { name: 'Oluştur' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('En az bir kota kapsamı etiketi')

    await user.type(labels, 'managed, tenant-2, managed')
    await user.click(screen.getByRole('button', { name: 'Oluştur' }))
    await waitFor(() => expect(storageApi.createQuotaPolicy).toHaveBeenCalledWith(expect.objectContaining({ key: 'managed-only', includedStorageScopes: [{ kind: 'quotaScope', values: ['managed', 'tenant-2'] }] })))
  })

  it('edits a policy to a storageConnection selector, validating and lowercasing GUIDs', async () => {
    const user = userEvent.setup()
    vi.mocked(storageApi.updateQuotaPolicy).mockResolvedValue({ policy: samplePolicy, tenantsRecalculated: 1 })
    renderPlatform(<PlatformQuotaPoliciesPage />, { path: '/platform/quota-policies', route: '/platform/quota-policies' })
    await screen.findByText('Default Policy')
    await user.click(screen.getByRole('button', { name: 'Düzenle' }))
    await user.selectOptions(await screen.findByLabelText(/Depolama Kapsamı/i), 'storageConnection')
    const ids = screen.getByLabelText(/Depolama bağlantısı kimlikleri/)

    await user.click(screen.getByRole('button', { name: 'Kaydet' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('En az bir depolama bağlantısı kimliği')

    await user.type(ids, 'not-a-guid')
    await user.click(screen.getByRole('button', { name: 'Kaydet' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('“not-a-guid” geçerli bir GUID değil')
    expect(storageApi.updateQuotaPolicy).not.toHaveBeenCalled()

    await user.clear(ids)
    await user.type(ids, '3F2B8C1E-0D4A-4B6E-9A7C-5E1D2F3A4B5C,\n11111111-2222-3333-4444-555555555555')
    await user.click(screen.getByRole('button', { name: 'Kaydet' }))
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Kaydet' }))
    await waitFor(() => expect(storageApi.updateQuotaPolicy).toHaveBeenCalledWith('policy-1', expect.objectContaining({
      includedStorageScopes: [{ kind: 'storageConnection', values: ['3f2b8c1e-0d4a-4b6e-9a7c-5e1d2f3a4b5c', '11111111-2222-3333-4444-555555555555'] }],
    })))
  })

  it('prefills an existing quotaScope selector and prints the new kinds in the list', async () => {
    const user = userEvent.setup()
    vi.mocked(storageApi.getQuotaPolicies).mockResolvedValue({ items: [
      { ...samplePolicy, includedStorageScopes: [{ kind: 'quotaScope', values: ['managed', 'tenant'] }] },
      { ...samplePolicy, policyId: 'policy-2', key: 'one', name: 'One connection', isDefault: false, includedStorageScopes: [{ kind: 'storageConnection', values: ['3f2b8c1e-0d4a-4b6e-9a7c-5e1d2f3a4b5c'] }] },
    ] })
    renderPlatform(<PlatformQuotaPoliciesPage />, { path: '/platform/quota-policies', route: '/platform/quota-policies' })
    expect(await screen.findByText('Kota kapsamı: managed, tenant')).toBeInTheDocument()
    const details = screen.getByText('Kota kapsamı: managed, tenant').closest('details')
    expect(details).not.toBeNull()
    expect(details).not.toHaveAttribute('open')
    await user.click(within(details!).getByText('Teknik ayrıntılar'))
    expect(within(details!).getByRole('button', { name: 'Kopyala: Kapsam' })).toBeInTheDocument()
    expect(screen.getByText('Depolama bağlantısı: 3f2b8c1e-0d4a-4b6e-9a7c-5e1d2f3a4b5c')).toBeInTheDocument()
    await user.click(screen.getAllByRole('button', { name: 'Düzenle' })[0])
    expect(await screen.findByLabelText(/Depolama Kapsamı/i)).toHaveValue('quotaScope')
    expect(screen.getByLabelText(/Kota kapsamı etiketleri/)).toHaveValue('managed, tenant')
  })
})
