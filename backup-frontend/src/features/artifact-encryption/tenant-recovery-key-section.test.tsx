import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { renderPlatform } from '@/features/platform/test-utils'
import { ApiError } from '@/lib/api/client'
import * as api from './api'
import { makeTenantEncryption } from './test-fixtures'
import { TenantKeysSection } from './tenant-recovery-key-section'

vi.mock('./api')
beforeEach(() => vi.resetAllMocks())
const mount = (encryption = makeTenantEncryption()) => renderPlatform(<TenantKeysSection tenantId="tenant-a" encryption={encryption} />)
const publicPem = '-----BEGIN PUBLIC KEY-----\ntest-public-key\n-----END PUBLIC KEY-----'

describe('tenant recovery public key', () => {
  it('warns when platform recovery is absent and states the RSA4096 requirement', () => {
    mount(makeTenantEncryption({ platformRecovery: { configured: false, keyId: null, keyVersion: null } }))
    expect(screen.getByText(/Platform kurtarma anahtarı yapılandırılmamış/)).toBeInTheDocument()
    expect(screen.getByText(/en az 4096 bit/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Anahtarı kaydet' })).toBeDisabled()
  })

  it('saves only the public key and clears the form after success', async () => {
    vi.mocked(api.setTenantRecoveryKey).mockResolvedValue(makeTenantEncryption())
    mount()
    const field = screen.getByRole('textbox', { name: 'Genel anahtar (PEM)' })
    fireEvent.change(field, { target: { value: publicPem } })
    fireEvent.click(screen.getByRole('button', { name: 'Anahtarı kaydet' }))
    await waitFor(() => expect(api.setTenantRecoveryKey).toHaveBeenCalledWith('tenant-a', publicPem))
    await waitFor(() => expect(field).toHaveValue(''))
    expect(screen.getByText(/Tenant kurtarma anahtarı kaydedildi/)).toBeInTheDocument()
  })

  it('rejects accidental private key entry before any API call with accessible feedback', () => {
    mount()
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
    fireEvent.change(screen.getByRole('textbox', { name: 'Genel anahtar (PEM)' }), { target: { value: publicPem } })
    fireEvent.click(screen.getByRole('button', { name: 'Anahtarı değiştir' }))
    await waitFor(() => expect(api.setTenantRecoveryKey).toHaveBeenCalled())
    expect(await screen.findByRole('alert')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Anahtarı kaldır' }))
    expect(api.removeTenantRecoveryKey).not.toHaveBeenCalled()
    fireEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Kaldır' }))
    await waitFor(() => expect(api.removeTenantRecoveryKey).toHaveBeenCalledWith('tenant-a'))
  })
})
