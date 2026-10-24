import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { renderPlatform } from '@/features/platform/test-utils'
import { ApiError } from '@/lib/api/client'
import * as api from './api'
import { makeWrapKey } from './test-fixtures'
import { WrapKeysSection } from './wrap-keys-section'

vi.mock('./api')
const keys = [makeWrapKey({ version: 2, state: 'Active', artifactCount: 1 }), makeWrapKey({ version: 1, keyId: 'c'.repeat(32), state: 'DecryptOnly', artifactCount: 7 }), makeWrapKey({ version: 0, keyId: 'd'.repeat(32), state: 'Retired', artifactCount: 0 })]
beforeEach(() => {
  vi.resetAllMocks()
  vi.mocked(api.getWrapKeys).mockResolvedValue({ items: keys })
})
const mount = () => renderPlatform(<WrapKeysSection tenantId="tenant-a" tenantName="Acme" />)

describe('tenant wrap key lifecycle', () => {
  it('lists versions with state, artifact impact and no key material', async () => {
    mount()
    const rows = await screen.findAllByRole('row')
    expect(rows).toHaveLength(4)
    expect(within(rows[1]).getByText('v2')).toBeInTheDocument()
    expect(within(rows[2]).getByText('7')).toBeInTheDocument()
    expect(document.body.textContent).not.toContain('c'.repeat(32))
    expect(screen.getByTitle('c'.repeat(32))).toHaveTextContent('cccccccccccc…')
  })

  it('cannot retire the active key and offers nothing for a retired one', async () => {
    mount()
    const rows = await screen.findAllByRole('row')
    expect(within(rows[1]).getByRole('button', { name: 'Emekli et' })).toBeDisabled()
    expect(within(rows[2]).getByRole('button', { name: 'Emekli et' })).toBeEnabled()
    expect(within(rows[3]).queryByRole('button')).not.toBeInTheDocument()
  })

  it('rotates only after confirmation and refreshes the list', async () => {
    vi.mocked(api.rotateWrapKey).mockResolvedValue({ key: keys[0], previousKey: keys[1] })
    mount()
    fireEvent.click(await screen.findByRole('button', { name: 'Anahtarı döndür' }))
    expect(api.rotateWrapKey).not.toHaveBeenCalled()
    fireEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Döndür' }))
    await waitFor(() => expect(api.rotateWrapKey).toHaveBeenCalledWith('tenant-a'))
    await waitFor(() => expect(api.getWrapKeys).toHaveBeenCalledTimes(2))
  })

  it('warns about the artifacts a retired key leaves unreadable online, then retires that version', async () => {
    vi.mocked(api.retireWrapKey).mockResolvedValue({ key: { ...keys[1], state: 'Retired' } })
    mount()
    const rows = await screen.findAllByRole('row')
    fireEvent.click(within(rows[2]).getByRole('button', { name: 'Emekli et' }))
    const dialog = await screen.findByRole('dialog')
    expect(dialog).toHaveTextContent('v1 hâlâ 7 yedeğin')
    expect(dialog).toHaveTextContent('çevrimdışı çözülebilir')
    fireEvent.click(within(dialog).getByRole('button', { name: 'Emekli et' }))
    await waitFor(() => expect(api.retireWrapKey).toHaveBeenCalledWith('tenant-a', 1))
  })

  it('shows the server reason when retiring is refused', async () => {
    vi.mocked(api.retireWrapKey).mockRejectedValue(new ApiError(409, { error: 'cannot_retire_active_key' }))
    mount()
    const rows = await screen.findAllByRole('row')
    fireEvent.click(within(rows[2]).getByRole('button', { name: 'Emekli et' }))
    fireEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Emekli et' }))
    expect(await screen.findByText('Etkin anahtar emekli edilemez. Önce anahtarı döndürün.')).toBeInTheDocument()
  })

  it('explains a tenant without encrypted backups yet', async () => {
    vi.mocked(api.getWrapKeys).mockResolvedValue({ items: [] })
    mount()
    expect(await screen.findByText(/henüz şifreli yedek almadı/)).toBeInTheDocument()
  })
})
