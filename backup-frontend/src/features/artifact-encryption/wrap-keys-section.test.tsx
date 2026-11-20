import userEvent from '@testing-library/user-event'
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
  it('sorts newest keys first and reverses the order from the creation header', async () => {
    vi.mocked(api.getWrapKeys).mockResolvedValue({ items: [
      makeWrapKey({ version: 1, state: 'DecryptOnly', artifactCount: 1, createdAt: '2026-01-01T00:00:00Z' }),
      makeWrapKey({ version: 2, state: 'Active', artifactCount: 2, createdAt: '2026-02-01T00:00:00Z' }),
    ] })
    mount()
    const rows = await screen.findAllByRole('row')
    expect(within(rows[1]).getByText('2')).toBeInTheDocument()
    const header = screen.getByRole('columnheader', { name: 'Oluşturulma' })
    expect(header).toHaveAttribute('aria-sort', 'descending')
    fireEvent.click(within(header).getByRole('button'))
    expect(within(screen.getAllByRole('row')[1]).getByText('1')).toBeInTheDocument()
    expect(header).toHaveAttribute('aria-sort', 'ascending')
  })

  it('lists versions with state, artifact impact and no key material', async () => {
    mount()
    const rows = await screen.findAllByRole('row')
    expect(rows).toHaveLength(4)
    expect(within(rows[2]).getByText('7')).toBeInTheDocument()
    expect(within(rows[2]).queryByText('c'.repeat(32))).toBeNull()
    await userEvent.click(within(rows[2]).getByRole('button', { name: 'Teknik ayrıntılar' }))
    const dialog = within(screen.getByRole('region', { name: 'Teknik ayrıntılar' }))
    expect(dialog.getByText('c'.repeat(32))).toBeVisible()
    expect(dialog.getByRole('button', { name: 'Kopyala' })).toBeInTheDocument()
  })

  it('cannot retire the active key and offers nothing for a retired one', async () => {
    mount()
    const rows = await screen.findAllByRole('row')
    expect(within(rows[1]).getByRole('button', { name: 'Emekli et' })).toBeDisabled()
    expect(within(rows[2]).getByRole('button', { name: 'Emekli et' })).toBeEnabled()
    expect(within(rows[3]).queryByRole('button', { name: 'Emekli et' })).not.toBeInTheDocument()
    fireEvent.click(within(rows[1]).getByRole('button', { name: 'Emekli et' }))
    expect(api.retireWrapKey).not.toHaveBeenCalled()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('rotates only after confirmation and refreshes the list', async () => {
    vi.mocked(api.rotateWrapKey).mockResolvedValue({ key: keys[0], previousKey: keys[1] })
    mount()
    fireEvent.click(await screen.findByRole('button', { name: 'Anahtarı döndür' }))
    expect(api.rotateWrapKey).not.toHaveBeenCalled()
    expect(within(await screen.findByRole('dialog')).queryByRole('textbox')).not.toBeInTheDocument()
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
    expect(dialog).toHaveTextContent('7')
    expect(dialog).toHaveTextContent('çevrimdışı')
    const confirm = within(dialog).getByRole('button', { name: 'Emekli et' })
    expect(confirm).toBeDisabled()
    fireEvent.change(within(dialog).getByRole('textbox'), { target: { value: 'acme' } })
    expect(confirm).toBeDisabled()
    expect(api.retireWrapKey).not.toHaveBeenCalled()
    fireEvent.change(within(dialog).getByRole('textbox'), { target: { value: 'Acme' } })
    fireEvent.click(confirm)
    await waitFor(() => expect(api.retireWrapKey).toHaveBeenCalledWith('tenant-a', 1))
  })

  it('shows the server reason when retiring is refused', async () => {
    vi.mocked(api.retireWrapKey).mockRejectedValue(new ApiError(409, { error: 'cannot_retire_active_key' }))
    mount()
    const rows = await screen.findAllByRole('row')
    fireEvent.click(within(rows[2]).getByRole('button', { name: 'Emekli et' }))
    const dialog = within(await screen.findByRole('dialog'))
    fireEvent.change(dialog.getByRole('textbox'), { target: { value: 'Acme' } })
    fireEvent.click(dialog.getByRole('button', { name: 'Emekli et' }))
    expect(await screen.findByText('Etkin anahtar emekli edilemez. Önce anahtarı döndürün.')).toBeInTheDocument()
  })

  it('explains a tenant without encrypted backups yet', async () => {
    vi.mocked(api.getWrapKeys).mockResolvedValue({ items: [] })
    mount()
    expect(await screen.findByText(/henüz şifreli yedek almadı/)).toBeInTheDocument()
  })
})
