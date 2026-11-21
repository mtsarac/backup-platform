import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { renderPlatform } from '@/features/platform/test-utils'
import { ApiError } from '@/lib/api/client'
import * as api from './api'
import { makeTenantEncryption } from './test-fixtures'
import { TenantKeysSection } from './tenant-recovery-key-section'

vi.mock('./api')
beforeEach(() => vi.resetAllMocks())
const mount = (encryption = makeTenantEncryption(), platformView = false) => renderPlatform(<TenantKeysSection tenantId="tenant-a" tenantName="Acme" encryption={encryption} platformView={platformView} />)
const publicPem = '-----BEGIN PUBLIC KEY-----\ndGVzdC1wdWJsaWMta2V5\n-----END PUBLIC KEY-----'

describe('tenant recovery public key', () => {
  it('shows a calm status when the tenant has its own recovery key and no platform-key warning', () => {
    mount(makeTenantEncryption({ platformRecovery: { configured: false, keyId: null, keyVersion: null }, tenantRecovery: { keyId: 'c'.repeat(32), keyVersion: 1, createdAt: '2026-10-19T10:00:00Z' } }))
    expect(screen.getByText('Kurtarma anahtarınız eklenmiş.')).toBeInTheDocument()
    expect(screen.queryByText('Kendi kurtarma anahtarınız yok.')).not.toBeInTheDocument()
    expect(screen.queryByText(/Platform kurtarma anahtarı yapılandırılmamış/)).not.toBeInTheDocument()
    expect(screen.queryByText('Şifreli yedekleriniz korunuyor')).not.toBeInTheDocument()
  })

  it('warns that the tenant has no recovery key and the primary button opens the add form', () => {
    mount(makeTenantEncryption({ platformRecovery: { configured: false, keyId: null, keyVersion: null } }))
    expect(screen.getByText('Kendi kurtarma anahtarınız yok.')).toBeInTheDocument()
    expect(screen.queryByText(/Platform kurtarma anahtarı yapılandırılmamış/)).not.toBeInTheDocument()
    expect(screen.getByLabelText('Genel anahtar (PEM)')).not.toBeVisible()
    fireEvent.click(screen.getByRole('button', { name: 'Kurtarma anahtarı ekle' }))
    expect(screen.getByLabelText('Genel anahtar (PEM)')).toBeVisible()
  })

  it('platform view keeps the platform-key warning and states the RSA4096 requirement', () => {
    mount(makeTenantEncryption({ platformRecovery: { configured: false, keyId: null, keyVersion: null } }), true)
    expect(screen.getByText(/Platform kurtarma anahtarı yapılandırılmamış/)).toBeInTheDocument()
    expect(screen.getByLabelText('Genel anahtar (PEM)')).not.toBeVisible()
    fireEvent.click(screen.getByText('Kendi anahtarınızı ekleyin veya değiştirin'))
    expect(screen.getByText(/en az 4096 bit/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Anahtarı kaydet' })).toBeDisabled()
  })

  it('saves only the public key and clears the form after success', async () => {
    vi.mocked(api.setTenantRecoveryKey).mockResolvedValue(makeTenantEncryption())
    mount()
    fireEvent.click(screen.getByText('Kendi anahtarınızı ekleyin veya değiştirin'))
    const field = screen.getByRole('textbox', { name: 'Genel anahtar (PEM)' })
    fireEvent.change(field, { target: { value: publicPem } })
    fireEvent.click(screen.getByRole('button', { name: 'Anahtarı kaydet' }))
    await waitFor(() => expect(api.setTenantRecoveryKey).toHaveBeenCalledWith('tenant-a', publicPem))
    await waitFor(() => expect(field).toHaveValue(''))
    expect(screen.getByText(/Kurtarma anahtarınız kaydedildi/)).toBeInTheDocument()
  })

  it('rejects accidental private key entry before any API call with accessible feedback', () => {
    mount()
    fireEvent.click(screen.getByText('Kendi anahtarınızı ekleyin veya değiştirin'))
    const field = screen.getByRole('textbox', { name: 'Genel anahtar (PEM)' })
    fireEvent.change(field, { target: { value: 'accidental PRIVATE KEY input' } })
    fireEvent.click(screen.getByRole('button', { name: 'Anahtarı kaydet' }))
    expect(api.setTenantRecoveryKey).not.toHaveBeenCalled()
    expect(field).toHaveAttribute('aria-invalid', 'true')
    expect(screen.getByText(/Özel anahtar kabul edilmez/)).toBeInTheDocument()
    expect(field).toHaveValue('')
  })

  it('keeps server validation visible and removes only after confirmation', async () => {
    vi.mocked(api.setTenantRecoveryKey).mockRejectedValue(new ApiError(400, { error: 'invalid_recovery_key', code: 'too_weak' }))
    vi.mocked(api.removeTenantRecoveryKey).mockResolvedValue(undefined)
    mount(makeTenantEncryption({ tenantRecovery: { keyId: 'c'.repeat(32), keyVersion: 2, createdAt: '2026-10-19T10:00:00Z' } }))
    fireEvent.click(screen.getByText('Kendi anahtarınızı ekleyin veya değiştirin'))
    fireEvent.change(screen.getByRole('textbox', { name: 'Genel anahtar (PEM)' }), { target: { value: publicPem } })
    fireEvent.click(screen.getByRole('button', { name: 'Anahtarı değiştir' }))
    await waitFor(() => expect(api.setTenantRecoveryKey).toHaveBeenCalled())
    expect(await screen.findByRole('alert')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Anahtarı kaldır' }))
    expect(api.removeTenantRecoveryKey).not.toHaveBeenCalled()
    const dialog = within(await screen.findByRole('dialog'))
    expect(dialog.getByRole('button', { name: 'Kaldır' })).toBeDisabled()
    fireEvent.change(dialog.getByRole('textbox'), { target: { value: 'Acme' } })
    fireEvent.click(dialog.getByRole('button', { name: 'Kaldır' }))
    await waitFor(() => expect(api.removeTenantRecoveryKey).toHaveBeenCalledWith('tenant-a'))
  })
})
