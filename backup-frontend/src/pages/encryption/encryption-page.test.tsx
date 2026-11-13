import userEvent from '@testing-library/user-event'
import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import * as api from '@/features/artifact-encryption/api'
import { keyId, makeTenantEncryption } from '@/features/artifact-encryption/test-fixtures'

import { renderPlatform } from '@/features/platform/test-utils'
import { TenantContext } from '@/features/tenancy/tenant-context'
import { EncryptionPage } from './encryption-page'

vi.mock('@/features/artifact-encryption/api')
function mount(canAdmin = true) {
  const tenantId = 'tenant-a'
  return renderPlatform(<TenantContext.Provider value={{ tenantId, tenant: { tenantId, name: 'Acme' }, canAdmin, canOperate: canAdmin, switchTenant: vi.fn() }}><EncryptionPage /></TenantContext.Provider>, { path: '/tenants/tenant-a/encryption' })
}
beforeEach(() => {
  vi.resetAllMocks()
  vi.mocked(api.getTenantEncryption).mockResolvedValue(makeTenantEncryption())
})

describe('tenant encryption page', () => {
  it('makes no request when access is denied', () => {
    mount(false)
    expect(screen.getByText('Bu alan için yetkiniz yok')).toBeInTheDocument()
    expect(screen.getByRole('heading', { level: 1 })).toBeInTheDocument()
    expect(api.getTenantEncryption).not.toHaveBeenCalled()
  })

  it('shows protection with copyable collapsed key details', async () => {
    mount()
    expect(await screen.findByText('Kurtarma anahtarınız eklenmemiş')).toBeInTheDocument()
    expect(screen.getByText('Şifreli yedekleriniz korunuyor')).toBeInTheDocument()
    expect(screen.queryByText(keyId)).not.toBeInTheDocument()
    await userEvent.click(screen.getAllByRole('button', { name: 'Teknik ayrıntılar' })[0])
    const details = await screen.findByRole('dialog')
    expect(within(details).getByRole('button', { name: 'Kopyala: Yedekleme anahtarı kimliği' })).toBeInTheDocument()
  })

  it('warns when the platform has no recovery key configured', async () => {
    vi.mocked(api.getTenantEncryption).mockResolvedValue(makeTenantEncryption({ platformRecovery: { configured: false, keyId: null, keyVersion: null }, wrapKey: null }))
    mount()
    expect(await screen.findByText(/Platform kurtarma anahtarı yapılandırılmamış: yeni şifreli/)).toBeInTheDocument()
    expect(screen.getByText('Henüz şifreli yedek oluşturulmadı')).toBeInTheDocument()
    expect(screen.queryByText('Platform kurtarma anahtarı hazır değil')).not.toBeInTheDocument()
    fireEvent.click(screen.getByText('Genel anahtarı yükle veya değiştir'))
    expect(screen.getByRole('button', { name: 'Anahtarı kaydet' })).toBeDisabled()
  })

  it('registers a recovery public key, trims it and clears the field', async () => {
    vi.mocked(api.setTenantRecoveryKey).mockResolvedValue(makeTenantEncryption({ tenantRecovery: { keyId: 'e'.repeat(32), keyVersion: 1, createdAt: '2026-10-19T10:00:00Z' } }))
    mount()
    fireEvent.click(await screen.findByText('Genel anahtarı yükle veya değiştir'))
    const pem = await screen.findByLabelText('Genel anahtar (PEM)')
    fireEvent.change(pem, { target: { value: '  -----BEGIN PUBLIC KEY-----\nabc\n-----END PUBLIC KEY-----\n ' } })
    fireEvent.click(screen.getByRole('button', { name: 'Anahtarı kaydet' }))
    await waitFor(() => expect(api.setTenantRecoveryKey).toHaveBeenCalledWith('tenant-a', '-----BEGIN PUBLIC KEY-----\nabc\n-----END PUBLIC KEY-----'))
    expect(await screen.findByText(/Kurtarma anahtarınız kaydedildi/)).toBeInTheDocument()
    expect(pem).toHaveValue('')
    expect(await screen.findByRole('button', { name: 'Anahtarı kaldır' })).toBeInTheDocument()
  })

  it.each(['PRIVATE KEY', 'RSA PRIVATE KEY', 'EC PRIVATE KEY', 'OPENSSH PRIVATE KEY', 'ENCRYPTED PRIVATE KEY'])('rejects %s before the API and wipes it', async (kind) => {
    mount()
    fireEvent.click(await screen.findByText('Genel anahtarı yükle veya değiştir'))
    const field = await screen.findByLabelText('Genel anahtar (PEM)')
    fireEvent.change(field, { target: { value: `-----BEGIN ${kind}-----\nsecret` } })
    fireEvent.click(screen.getByRole('button', { name: 'Anahtarı kaydet' }))
    expect(await screen.findByText(/Özel anahtar kabul edilmez/)).toBeInTheDocument()
    expect(api.setTenantRecoveryKey).not.toHaveBeenCalled()
    expect(field).toHaveValue('')
    expect(screen.queryByText(/Anahtar kaydedildi/)).not.toBeInTheDocument()
  })

  it('loads a public key file and rejects a private key file without sending it', async () => {
    vi.mocked(api.setTenantRecoveryKey).mockResolvedValue(makeTenantEncryption())
    mount()
    fireEvent.click(await screen.findByText('Genel anahtarı yükle veya değiştir'))
    const upload = await screen.findByLabelText('Genel anahtar dosyası')
    fireEvent.change(upload, { target: { files: [new File(['-----BEGIN RSA PRIVATE KEY-----\nsecret'], 'private.pem')] } })
    expect(await screen.findByText(/Özel anahtar kabul edilmez/)).toBeInTheDocument()
    expect(api.setTenantRecoveryKey).not.toHaveBeenCalled()
    expect(screen.getByLabelText('Genel anahtar (PEM)')).toHaveValue('')
    const publicPem = '-----BEGIN PUBLIC KEY-----\nabc\n-----END PUBLIC KEY-----'
    fireEvent.change(upload, { target: { files: [new File([publicPem], 'public.pem')] } })
    await waitFor(() => expect(screen.getByLabelText('Genel anahtar (PEM)')).toHaveValue(publicPem))
    fireEvent.click(screen.getByRole('button', { name: 'Anahtarı kaydet' }))
    await waitFor(() => expect(api.setTenantRecoveryKey).toHaveBeenCalledWith('tenant-a', publicPem))
  })

  it('does not send input that is not a public PEM key', async () => {
    mount()
    fireEvent.click(await screen.findByText('Genel anahtarı yükle veya değiştir'))
    const field = await screen.findByLabelText('Genel anahtar (PEM)')
    fireEvent.change(field, { target: { value: 'not a public key' } })
    fireEvent.click(screen.getByRole('button', { name: 'Anahtarı kaydet' }))
    expect(api.setTenantRecoveryKey).not.toHaveBeenCalled()
    expect(field).toHaveAttribute('aria-invalid', 'true')
  })

  it('removes the recovery key only after confirmation', async () => {
    vi.mocked(api.getTenantEncryption).mockResolvedValue(makeTenantEncryption({ tenantRecovery: { keyId: 'e'.repeat(32), keyVersion: 2, createdAt: '2026-10-19T10:00:00Z' } }))
    vi.mocked(api.removeTenantRecoveryKey).mockResolvedValue(undefined)
    mount()
    fireEvent.click(await screen.findByRole('button', { name: 'Anahtarı kaldır' }))
    expect(api.removeTenantRecoveryKey).not.toHaveBeenCalled()
    const dialog = within(await screen.findByRole('dialog'))
    const confirm = dialog.getByRole('button', { name: 'Kaldır' })
    expect(confirm).toBeDisabled()
    fireEvent.change(dialog.getByRole('textbox'), { target: { value: 'acme' } })
    expect(confirm).toBeDisabled()
    fireEvent.change(dialog.getByRole('textbox'), { target: { value: 'Acme' } })
    fireEvent.click(confirm)
    await waitFor(() => expect(api.removeTenantRecoveryKey).toHaveBeenCalledWith('tenant-a'))
  })
})
