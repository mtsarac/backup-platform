import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import * as api from '@/features/artifact-encryption/api'
import { keyId, makeTenantEncryption } from '@/features/artifact-encryption/test-fixtures'
import type { MembershipRole } from '@/features/platform/types'
import { paged, renderPlatform } from '@/features/platform/test-utils'
import { TenantContext } from '@/features/tenancy/tenant-context'
import { ApiError } from '@/lib/api/client'
import { EncryptionPage } from './encryption-page'

vi.mock('@/features/artifact-encryption/api')
const event = { id: 'event-a', artifactId: '11111111-1111-1111-1111-111111111111', status: 'Consumed' as const, reason: 'Incident 4711', createdAt: '2026-10-19T10:00:00Z', expiresAt: '2026-10-19T11:00:00Z', requestedByName: 'Ada Admin', decidedByName: 'Bo Admin', decidedAt: '2026-10-19T10:10:00Z', consumedAt: '2026-10-19T10:20:00Z' }
function mount(role: MembershipRole = 'Owner') {
  const tenantId = 'tenant-a'
  return renderPlatform(<TenantContext.Provider value={{ tenantId, tenant: { tenantId, name: 'Acme', role }, canAdmin: role === 'Owner' || role === 'Admin', canOperate: role !== 'Viewer', switchTenant: vi.fn() }}><EncryptionPage /></TenantContext.Provider>, { path: '/tenants/tenant-a/encryption' })
}
beforeEach(() => {
  vi.resetAllMocks()
  vi.mocked(api.getTenantEncryption).mockResolvedValue(makeTenantEncryption())
  vi.mocked(api.getTenantBreakGlass).mockResolvedValue(paged([event]))
})

describe('tenant encryption page', () => {
  it.each(['Operator', 'Viewer'] as const)('makes no request for %s', (role) => {
    mount(role)
    expect(screen.getByText('Bu alan için yetkiniz yok')).toBeInTheDocument()
    expect(api.getTenantEncryption).not.toHaveBeenCalled()
    expect(api.getTenantBreakGlass).not.toHaveBeenCalled()
  })

  it.each(['Owner', 'Admin'] as const)('shows key ids only and the break-glass history for %s', async (role) => {
    mount(role)
    expect(await screen.findByText('Incident 4711')).toBeInTheDocument()
    expect(screen.getByText('Ada Admin')).toBeInTheDocument()
    expect(screen.getByText(/Bo Admin/)).toBeInTheDocument()
    expect(screen.getByText('Tanımlı değil')).toBeInTheDocument()
    expect(document.body.textContent).not.toContain(keyId)
    expect(screen.getByRole('link', { name: '11111111…' })).toHaveAttribute('href', '/tenants/tenant-a/artifacts/11111111-1111-1111-1111-111111111111')
  })

  it('warns when the platform has no recovery key configured', async () => {
    vi.mocked(api.getTenantEncryption).mockResolvedValue(makeTenantEncryption({ platformRecovery: { configured: false, keyId: null, keyVersion: null }, wrapKey: null }))
    mount()
    expect(await screen.findByText(/Platform kurtarma anahtarı yapılandırılmamış: yeni şifreli/)).toBeInTheDocument()
    expect(screen.getByText(/Henüz oluşturulmadı/)).toBeInTheDocument()
  })

  it('registers a recovery public key, trims it and clears the field', async () => {
    vi.mocked(api.setTenantRecoveryKey).mockResolvedValue(makeTenantEncryption({ tenantRecovery: { keyId: 'e'.repeat(32), keyVersion: 1, createdAt: '2026-10-19T10:00:00Z' } }))
    mount()
    const pem = await screen.findByLabelText('Genel anahtar (PEM)')
    fireEvent.change(pem, { target: { value: '  -----BEGIN PUBLIC KEY-----\nabc\n-----END PUBLIC KEY-----\n ' } })
    fireEvent.click(screen.getByRole('button', { name: 'Anahtarı kaydet' }))
    await waitFor(() => expect(api.setTenantRecoveryKey).toHaveBeenCalledWith('tenant-a', '-----BEGIN PUBLIC KEY-----\nabc\n-----END PUBLIC KEY-----'))
    expect(await screen.findByText(/Tenant kurtarma anahtarı kaydedildi/)).toBeInTheDocument()
    expect(pem).toHaveValue('')
    expect(await screen.findByRole('button', { name: 'Anahtarı kaldır' })).toBeInTheDocument()
  })

  it('shows a clear reason when a private key was pasted, and does not echo it', async () => {
    vi.mocked(api.setTenantRecoveryKey).mockRejectedValue(new ApiError(400, { error: 'invalid_recovery_key', code: 'private_key_rejected' }))
    mount()
    fireEvent.change(await screen.findByLabelText('Genel anahtar (PEM)'), { target: { value: '-----BEGIN PRIVATE KEY-----\nsecret' } })
    fireEvent.click(screen.getByRole('button', { name: 'Anahtarı kaydet' }))
    expect(await screen.findByText(/Özel anahtar kabul edilmez/)).toBeInTheDocument()
    expect(screen.queryByText(/Anahtar kaydedildi/)).not.toBeInTheDocument()
  })

  it('removes the recovery key only after confirmation', async () => {
    vi.mocked(api.getTenantEncryption).mockResolvedValue(makeTenantEncryption({ tenantRecovery: { keyId: 'e'.repeat(32), keyVersion: 2, createdAt: '2026-10-19T10:00:00Z' } }))
    vi.mocked(api.removeTenantRecoveryKey).mockResolvedValue(undefined)
    mount()
    fireEvent.click(await screen.findByRole('button', { name: 'Anahtarı kaldır' }))
    expect(api.removeTenantRecoveryKey).not.toHaveBeenCalled()
    fireEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Kaldır' }))
    await waitFor(() => expect(api.removeTenantRecoveryKey).toHaveBeenCalledWith('tenant-a'))
  })
})
