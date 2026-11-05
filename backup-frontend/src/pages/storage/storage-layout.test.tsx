import { screen, within } from '@testing-library/react'
import { render } from '@/test/render'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { getTenantStorageQuota } from '@/features/storage/api'
import type { TenantQuota } from '@/features/storage/types'
import * as connectionApi from '@/features/storage-connections/api'
import { catalog, makeConnection, makeManaged } from '@/features/storage-connections/test-fixtures'
import { ConnectionList } from '@/features/storage-connections/connection-list'
import { ApiError } from '@/lib/api/client'
import { StorageLayout } from './storage-layout'
import { StorageUsageTab } from './storage-usage-tab'
import { StorageProvidersTab } from './storage-providers-tab'

vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => ({ tenantId: 'tenant-a', tenant: { name: 'Acme A.Ş.' }, canAdmin: true, canOperate: true }) }))
vi.mock('@/features/storage/api')
vi.mock('@/features/storage-connections/api')

const quota: TenantQuota = {
  limitBytes: 10_000_000_000, unlimited: false, usedBytes: 6_200_000_000, reservedBytes: 0, remainingBytes: 3_800_000_000, state: 'Normal', warningThreshold: 80, criticalThreshold: 95,
  accountingMode: 'LogicalArtifactBytes', enforcementMode: 'BlockNewBackups', subscriptionStatus: 'Active', newBackupsAllowed: true, denialCode: null,
  plan: { name: 'Standard 10 GB', isSystem: false, systemKey: null, subscriptionEndsAt: null }, includedStorageScopes: [{ kind: 'quotaScope', values: ['managed'] }], calculatedAt: '2026-10-08T12:00:00Z',
}

function mount(suffix = '') {
  render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}><MemoryRouter initialEntries={[`/tenants/tenant-a/storage${suffix}`]}><Routes>
    <Route path="/tenants/tenant-a/storage" element={<StorageLayout />}>
      <Route index element={<ConnectionList />} /><Route path="usage" element={<StorageUsageTab />} /><Route path="providers" element={<StorageProvidersTab />} />
    </Route>
  </Routes></MemoryRouter></QueryClientProvider>)
}

beforeEach(() => {
  vi.resetAllMocks()
  vi.mocked(getTenantStorageQuota).mockResolvedValue(quota)
  vi.mocked(connectionApi.getTenantConnections).mockResolvedValue({ items: [makeConnection(), makeManaged()] })
  vi.mocked(connectionApi.getTenantProviders).mockResolvedValue({ items: catalog })
})

describe('Storage hub', () => {
  it('keeps destinations, quotas and provider capabilities accessible on separate subpages', async () => {
    mount()
    expect(await screen.findByRole('article', { name: "Managed Storage" })).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Plan ve abonelik' })).not.toBeInTheDocument()
    await userEvent.click(screen.getByRole('link', { name: 'Kullanım ve kota' }))
    expect(await screen.findByRole('heading', { name: 'Plan ve abonelik' })).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Kullanım' })).toBeInTheDocument()
    expect(screen.getByText('Kota kapsamı: managed')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('link', { name: 'Sağlayıcılar' }))
    expect(await screen.findByRole('heading', { name: 'SFTP' })).toBeInTheDocument()
  })

  it('does not make destinations depend on a successful quota read', async () => {
    vi.mocked(getTenantStorageQuota).mockRejectedValue(new ApiError(500))
    mount()
    expect(await screen.findByRole('article', { name: "Local" })).toBeInTheDocument()
    expect(getTenantStorageQuota).not.toHaveBeenCalled()
  })

  it('rejects non-member quota reads without requesting destinations', async () => {
    vi.mocked(getTenantStorageQuota).mockRejectedValue(new ApiError(403))
    mount('/usage')
    expect(await screen.findByText("Bu çalışma alanının depolama bilgilerine erişiminiz yok")).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Depolama hedefleri' })).not.toBeInTheDocument()
    expect(connectionApi.getTenantConnections).not.toHaveBeenCalled()
  })

  it('names the hub once and offers one add action on every tab', async () => {
    mount()
    expect(await screen.findByRole('heading', { level: 1, name: 'Depolama' })).toBeInTheDocument()
    expect(screen.queryByText(/Depolama hedefleri, kullanım ve kota/)).not.toBeInTheDocument()
    expect(screen.getAllByRole('link', { name: 'Bağlantı ekle' })).toHaveLength(1)
    await userEvent.click(screen.getByRole('link', { name: 'Kullanım ve kota' }))
    await screen.findByRole('heading', { name: 'Plan ve abonelik' })
    expect(screen.getAllByRole('link', { name: 'Bağlantı ekle' })).toHaveLength(1)
  })

  it('on the providers tab marks a planned provider once, with the sentence only', async () => {
    mount('/providers')
    const card = (await screen.findByRole('heading', { name: 'SFTP' })).closest('li')!
    expect(within(card).getByText(/Yakında \/ henüz kullanılamaz/)).toBeInTheDocument()
    expect(within(card).queryByText('Yakında')).not.toBeInTheDocument()
  })

  it('shows the quota state, thresholds, subscription status and API eligibility once each on the usage tab', async () => {
    vi.mocked(getTenantStorageQuota).mockResolvedValue({ ...quota, state: 'Warning', usedBytes: 8_500_000_000, remainingBytes: 1_500_000_000 })
    mount('/usage')
    await screen.findByRole('heading', { name: 'Plan ve abonelik' })
    expect(screen.getAllByText('Uyarı')).toHaveLength(1)
    expect(screen.getAllByText(/%80/)).toHaveLength(1)
    expect(screen.getAllByText('Etkin')).toHaveLength(1)
    expect(screen.queryByText('Kota durumu')).not.toBeInTheDocument()
    expect(screen.getAllByText('Yeni yedekleme')).toHaveLength(1)
    expect(screen.getByText('Yeni yedekleme').nextElementSibling).toHaveTextContent('Evet')
    expect(screen.getByText('Kapasite dolmaya yaklaşıyor')).toBeInTheDocument()
  })

  it.each([
    [false, 'Normal', 'Hayır'],
    [true, 'OverQuota', 'Evet'],
  ] as const)('shows API newBackupsAllowed=%s without inferring it from the %s quota state or active subscription', async (newBackupsAllowed, state, result) => {
    vi.mocked(getTenantStorageQuota).mockResolvedValue({ ...quota, state, newBackupsAllowed })
    mount('/usage')
    const label = await screen.findByText('Yeni yedekleme')
    expect(label.nextElementSibling).toHaveTextContent(result)
    expect(screen.getAllByText('Yeni yedekleme')).toHaveLength(1)
    expect(screen.getByText('Etkin')).toBeInTheDocument()
  })

  it('says existing backups are kept once when a suspended subscription and a full quota both block new backups', async () => {
    vi.mocked(getTenantStorageQuota).mockResolvedValue({ ...quota, subscriptionStatus: 'Suspended', state: 'OverQuota', newBackupsAllowed: false, denialCode: 'subscription_required', usedBytes: 10_000_000_000, remainingBytes: 0 })
    mount('/usage')
    expect(await screen.findByText('Abonelik askıda')).toBeInTheDocument()
    expect(screen.getByText(/Depolama kotası doldu/)).toBeInTheDocument()
    expect(screen.getAllByText(/silinmez ve korunur/)).toHaveLength(1)
    expect(screen.getAllByText('Askıda')).toHaveLength(1)
    expect(screen.getAllByText('Kota aşıldı')).toHaveLength(1)
  })

  it('does not repeat the legacy unlimited plan or show thresholds that cannot apply without a limit', async () => {
    vi.mocked(getTenantStorageQuota).mockResolvedValue({ ...quota, limitBytes: null, unlimited: true, remainingBytes: null, state: 'Unlimited', plan: { name: 'Legacy Unlimited', isSystem: true, systemKey: 'legacy_unlimited', subscriptionEndsAt: null } })
    mount('/usage')
    await screen.findByRole('heading', { name: 'Plan ve abonelik' })
    expect(screen.getAllByText('Legacy Unlimited')).toHaveLength(1)
    expect(screen.getAllByText('Eski Sınırsız')).toHaveLength(1)
    expect(screen.queryByText('Eşikler')).not.toBeInTheDocument()
    expect(screen.queryByText(/eşikler uygulanmaz/)).not.toBeInTheDocument()
    expect(screen.queryByText(/\(Legacy Unlimited\)/)).not.toBeInTheDocument()
  })
})
