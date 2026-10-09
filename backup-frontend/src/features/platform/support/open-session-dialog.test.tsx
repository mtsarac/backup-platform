import { beforeEach, describe, expect, it, vi } from 'vitest'
import { act, fireEvent, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { ApiError } from '@/lib/api/client'
import { platformPaths } from '../paths'
import { paged, renderPlatform } from '../test-utils'
import * as api from './api'
import { OpenSessionDialog } from './open-session-dialog'
import { SupportAccessSection } from './support-access-section'
import type { SupportSession } from './types'

vi.mock('./api')

const firstKey = '00000000-0000-4000-8000-000000000001'
const secondKey = '00000000-0000-4000-8000-000000000002'
const session: SupportSession = {
  id: 'session-a', tenantId: 'tenant-a', tenantName: 'Acme A.Ş.', platformAdminId: 'admin-id', actorDisplayName: 'Platform Operatörü',
  reason: 'Backup incelemesi', permissions: ['ViewBackupMetadata'], startedAt: '2026-10-09T10:00:00Z', expiresAt: '2026-10-09T11:00:00Z',
  status: 'Active', closedAt: null, closedBy: null, closedByDisplayName: null, closeReason: null,
  createdAt: '2026-10-09T10:00:00Z', isOwner: true, serverNow: '2026-10-09T10:00:00Z',
}

function mountDialog() {
  const onClose = vi.fn()
  const view = renderPlatform(<OpenSessionDialog tenantId="tenant-a" tenantName="Acme A.Ş." onClose={onClose} />)
  return { ...view, onClose }
}

async function dialogForm() {
  const dialog = await screen.findByRole('dialog', { name: 'Destek oturumu aç' })
  const form = within(dialog).getByRole('button', { name: 'Oturumu aç' }).closest('form')
  if (!form) throw new Error('Support session form not found')
  return { dialog, form, fields: within(dialog) }
}

beforeEach(() => {
  vi.restoreAllMocks()
  vi.resetAllMocks()
  vi.spyOn(crypto, 'randomUUID').mockReturnValue(firstKey)
  vi.mocked(api.openSession).mockResolvedValue(session)
  vi.mocked(api.getSessions).mockResolvedValue(paged([session], { pageSize: 5 }))
})

describe('support session form', () => {
  it('opens immediately and shows Turkish permission labels, default duration and all presets', async () => {
    mountDialog()
    const { fields } = await dialogForm()
    expect(fields.getByText(/Acme A.Ş./)).toBeInTheDocument()
    const labels = fields.getAllByRole('checkbox').map((checkbox) => checkbox.closest('label')?.textContent)
    expect(labels).toHaveLength(3)
    for (const permission of ['ViewBackupMetadata', 'DownloadBackup', 'RestoreBackup']) {
      const label = labels.find((value) => value?.includes(permission))
      expect(label?.replace(permission, '').trim().length).toBeGreaterThan(0)
    }
    const duration = fields.getByRole('spinbutton', { name: 'Süre (dakika)' })
    expect(duration).toHaveValue(60)
    expect(duration).toHaveAttribute('max', '240')
    const listId = duration.getAttribute('list')
    if (!listId) throw new Error('Duration presets not found')
    const presets = document.getElementById(listId)
    expect(Array.from(presets?.querySelectorAll('option') ?? []).map((option) => option.value)).toEqual(['15', '30', '60', '120', '240'])
  })

  it('shows the fixed tenant as a readonly field that cannot be edited', async () => {
    mountDialog()
    const { fields } = await dialogForm()
    const tenant = fields.getByRole('textbox', { name: 'Tenant' })
    expect(tenant).toHaveValue('Acme A.Ş.')
    expect(tenant).toHaveAttribute('readonly')
    await userEvent.type(tenant, 'Başka tenant')
    expect(tenant).toHaveValue('Acme A.Ş.')
    expect(fields.getByRole('textbox', { name: 'Erişim gerekçesi' })).toHaveValue('')
  })

  it.each(['', '   \n\t '])('rejects an empty or whitespace-only reason (%j)', async (reason) => {
    mountDialog()
    const { fields } = await dialogForm()
    fireEvent.change(fields.getByRole('textbox', { name: 'Erişim gerekçesi' }), { target: { value: reason } })
    await userEvent.click(fields.getByRole('button', { name: 'Oturumu aç' }))
    expect(await fields.findByRole('alert')).toHaveTextContent('Destek erişimi için gerekçe girin.')
    expect(api.openSession).not.toHaveBeenCalled()
  })

  it('enforces the 500-character reason limit even on a programmatic submit', async () => {
    mountDialog()
    const { fields, form } = await dialogForm()
    const reason = fields.getByRole('textbox', { name: 'Erişim gerekçesi' })
    expect(reason).toHaveAttribute('maxlength', '500')
    fireEvent.change(reason, { target: { value: 'a'.repeat(501) } })
    fireEvent.submit(form)
    expect(await fields.findByRole('alert')).toHaveTextContent('Gerekçe en fazla 500 karakter olabilir.')
    expect(api.openSession).not.toHaveBeenCalled()
  })

  it('requires at least one permission', async () => {
    mountDialog()
    const { fields } = await dialogForm()
    fireEvent.change(fields.getByRole('textbox', { name: 'Erişim gerekçesi' }), { target: { value: 'Backup incelemesi' } })
    await userEvent.click(fields.getByRole('checkbox', { name: /ViewBackupMetadata/ }))
    await userEvent.click(fields.getByRole('button', { name: 'Oturumu aç' }))
    expect(await fields.findByRole('alert')).toHaveTextContent('En az bir izin seçin.')
    expect(api.openSession).not.toHaveBeenCalled()
  })

  it.each(['DownloadBackup', 'RestoreBackup'])('automatically adds ViewBackupMetadata for %s and prevents unchecking it', async (permission) => {
    mountDialog()
    const { fields } = await dialogForm()
    const view = fields.getByRole('checkbox', { name: /ViewBackupMetadata/ })
    const dependent = fields.getByRole('checkbox', { name: new RegExp(permission) })
    await userEvent.click(view)
    expect(view).not.toBeChecked()
    await userEvent.click(dependent)
    expect(view).toBeChecked()
    expect(view).toBeDisabled()
    await userEvent.click(view)
    expect(view).toBeChecked()
    await userEvent.click(dependent)
    expect(view).toBeEnabled()
    await userEvent.click(view)
    expect(view).not.toBeChecked()
  })

  it('keeps the metadata permission locked until both dependent permissions are cleared', async () => {
    mountDialog()
    const { fields } = await dialogForm()
    const view = fields.getByRole('checkbox', { name: /ViewBackupMetadata/ })
    const download = fields.getByRole('checkbox', { name: /DownloadBackup/ })
    const restore = fields.getByRole('checkbox', { name: /RestoreBackup/ })
    await userEvent.click(download)
    await userEvent.click(restore)
    await userEvent.click(download)
    expect(view).toBeChecked()
    expect(view).toBeDisabled()
    await userEvent.click(restore)
    expect(view).toBeEnabled()
  })

  it.each(['241', '0', '1.5', ''])('rejects invalid duration %j before making a request', async (duration) => {
    mountDialog()
    const { fields, form } = await dialogForm()
    fireEvent.change(fields.getByRole('textbox', { name: 'Erişim gerekçesi' }), { target: { value: 'Backup incelemesi' } })
    fireEvent.change(fields.getByRole('spinbutton', { name: 'Süre (dakika)' }), { target: { value: duration } })
    // Bypass native constraint validation to exercise the explicit client-side maximum and integer check.
    fireEvent.submit(form)
    expect(await fields.findByRole('alert')).toHaveTextContent('Süre 1 ile 240 dakika arasında tam sayı olmalıdır.')
    expect(api.openSession).not.toHaveBeenCalled()
  })

  it('also explains the duration limit when native validation rejects a normal button click', async () => {
    mountDialog()
    const { fields } = await dialogForm()
    fireEvent.change(fields.getByRole('textbox', { name: 'Erişim gerekçesi' }), { target: { value: 'Backup incelemesi' } })
    fireEvent.change(fields.getByRole('spinbutton', { name: 'Süre (dakika)' }), { target: { value: '241' } })
    await userEvent.click(fields.getByRole('button', { name: 'Oturumu aç' }))
    expect(await fields.findByRole('alert')).toHaveTextContent('Süre 1 ile 240 dakika arasında tam sayı olmalıdır.')
    expect(api.openSession).not.toHaveBeenCalled()
  })

  it('trims the reason, accepts 240 minutes and navigates to the returned session after closing', async () => {
    const { onClose } = mountDialog()
    const { fields } = await dialogForm()
    fireEvent.change(fields.getByRole('textbox', { name: 'Erişim gerekçesi' }), { target: { value: '  Backup incelemesi  ' } })
    fireEvent.change(fields.getByRole('spinbutton', { name: 'Süre (dakika)' }), { target: { value: '240' } })
    await userEvent.click(fields.getByRole('checkbox', { name: /DownloadBackup/ }))
    await userEvent.click(fields.getByRole('button', { name: 'Oturumu aç' }))
    await waitFor(() => expect(api.openSession).toHaveBeenCalledWith({
      tenantId: 'tenant-a', reason: 'Backup incelemesi', permissions: ['ViewBackupMetadata', 'DownloadBackup'], durationMinutes: 240,
    }, firstKey))
    await waitFor(() => expect(onClose).toHaveBeenCalledOnce())
    expect(screen.getByTestId('location')).toHaveTextContent(platformPaths.supportSession(session.id))
    expect(crypto.randomUUID).toHaveBeenCalledTimes(2)
  })

  it('makes only one request on immediate double-submit, disables interaction while pending and reuses the key after failure', async () => {
    let rejectRequest: ((error: unknown) => void) | undefined
    vi.mocked(api.openSession).mockReturnValueOnce(new Promise<SupportSession>((resolve, reject) => {
      void resolve
      rejectRequest = reject
    }))
    const { onClose } = mountDialog()
    const { fields, form } = await dialogForm()
    fireEvent.change(fields.getByRole('textbox', { name: 'Erişim gerekçesi' }), { target: { value: 'Backup incelemesi' } })
    act(() => { fireEvent.submit(form); fireEvent.submit(form) })
    await waitFor(() => expect(api.openSession).toHaveBeenCalledTimes(1))
    expect(fields.getByRole('button', { name: 'Oturum açılıyor…' })).toBeDisabled()
    expect(fields.getByRole('button', { name: 'Vazgeç' })).toBeDisabled()
    expect(fields.getByRole('textbox', { name: 'Erişim gerekçesi' })).toBeDisabled()
    expect(fields.getByRole('spinbutton', { name: 'Süre (dakika)' })).toBeDisabled()
    expect(onClose).not.toHaveBeenCalled()
    const reject = rejectRequest
    if (!reject) throw new Error('Support request was not started')
    await act(async () => { reject(new ApiError(503, { error: 'support_unavailable' })) })
    expect(await fields.findByRole('alert')).toHaveTextContent('audit kaydı oluşturulamadığı için kullanılamıyor')
    await userEvent.click(fields.getByRole('button', { name: 'Oturumu aç' }))
    await waitFor(() => expect(api.openSession).toHaveBeenCalledTimes(2))
    expect(vi.mocked(api.openSession).mock.calls.map((call) => call[1])).toEqual([firstKey, firstKey])
  })

  it('shows a safe active-session conflict with a link to the existing session', async () => {
    vi.mocked(api.openSession).mockRejectedValue(new ApiError(409, { error: 'active_session_exists', sessionId: 'existing-session' }))
    const { onClose } = mountDialog()
    const { fields } = await dialogForm()
    fireEvent.change(fields.getByRole('textbox', { name: 'Erişim gerekçesi' }), { target: { value: 'Backup incelemesi' } })
    await userEvent.click(fields.getByRole('button', { name: 'Oturumu aç' }))
    const alert = await fields.findByRole('alert')
    expect(alert).toHaveTextContent('Bu tenant için zaten etkin bir destek oturumunuz var.')
    expect(within(alert).getByRole('link', { name: 'Mevcut destek oturumuna git' })).toHaveAttribute('href', platformPaths.supportSession('existing-session'))
    expect(onClose).not.toHaveBeenCalled()
  })

  it('does not close or navigate when the form unmounts before the request succeeds', async () => {
    const view = mountDialog()
    vi.mocked(api.openSession).mockImplementationOnce(async () => {
      view.unmount()
      return session
    })
    const { fields, form } = await dialogForm()
    fireEvent.change(fields.getByRole('textbox', { name: 'Erişim gerekçesi' }), { target: { value: 'Backup incelemesi' } })
    fireEvent.submit(form)
    await waitFor(() => expect(view.client.getMutationCache().getAll()[0]?.state.status).toBe('success'))
    expect(view.onClose).not.toHaveBeenCalled()
    expect(crypto.randomUUID).toHaveBeenCalledOnce()
  })

  it('never displays raw error text', async () => {
    vi.mocked(api.openSession).mockRejectedValue(new Error('Server exception: private credentials'))
    mountDialog()
    const { fields } = await dialogForm()
    fireEvent.change(fields.getByRole('textbox', { name: 'Erişim gerekçesi' }), { target: { value: 'Backup incelemesi' } })
    await userEvent.click(fields.getByRole('button', { name: 'Oturumu aç' }))
    expect(await fields.findByRole('alert')).toHaveTextContent('Sunucuya ulaşılamadı')
    expect(screen.queryByText(/private credentials/)).not.toBeInTheDocument()
  })
})

describe('tenant support access section', () => {
  it('loads the five most recent tenant sessions without a tenant column and links to the filtered full list', async () => {
    renderPlatform(<SupportAccessSection tenantId="tenant-a" tenantName="Acme A.Ş." />)
    const table = await screen.findByRole('table')
    expect(api.getSessions).toHaveBeenCalledWith({ tenantId: 'tenant-a', page: 1, pageSize: 5 }, expect.any(AbortSignal))
    expect(within(table).queryByRole('columnheader', { name: 'Tenant' })).not.toBeInTheDocument()
    expect(within(table).getByText(session.reason)).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Tüm destek oturumları' })).toHaveAttribute('href', `${platformPaths.supportSessions}?tenantId=tenant-a`)
    expect(screen.getByText(/yalnızca gerekçeli, süreli/)).toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('uses a new UUID and resets the form after closing and reopening', async () => {
    vi.mocked(crypto.randomUUID).mockReturnValueOnce(firstKey).mockReturnValueOnce(secondKey)
    vi.mocked(api.openSession).mockRejectedValue(new ApiError(503))
    renderPlatform(<SupportAccessSection tenantId="tenant-a" tenantName="Acme A.Ş." />)
    await screen.findByRole('table')
    await userEvent.click(screen.getByRole('button', { name: 'Destek oturumu aç' }))
    const first = await dialogForm()
    fireEvent.change(first.fields.getByRole('textbox', { name: 'Erişim gerekçesi' }), { target: { value: 'İlk inceleme' } })
    await userEvent.click(first.fields.getByRole('button', { name: 'Oturumu aç' }))
    await first.fields.findByRole('alert')
    await userEvent.click(first.fields.getByRole('button', { name: 'Vazgeç' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    await userEvent.click(screen.getByRole('button', { name: 'Destek oturumu aç' }))
    const second = await dialogForm()
    expect(second.fields.getByRole('textbox', { name: 'Erişim gerekçesi' })).toHaveValue('')
    expect(second.fields.getByRole('spinbutton', { name: 'Süre (dakika)' })).toHaveValue(60)
    expect(second.fields.queryByRole('alert')).not.toBeInTheDocument()
    fireEvent.change(second.fields.getByRole('textbox', { name: 'Erişim gerekçesi' }), { target: { value: 'İkinci inceleme' } })
    await userEvent.click(second.fields.getByRole('button', { name: 'Oturumu aç' }))
    await waitFor(() => expect(api.openSession).toHaveBeenCalledTimes(2))
    expect(vi.mocked(api.openSession).mock.calls.map((call) => call[1])).toEqual([firstKey, secondKey])
  })

  it('renders a pending query and a safe query failure', async () => {
    vi.mocked(api.getSessions).mockReturnValueOnce(new Promise(() => undefined))
    const pending = renderPlatform(<SupportAccessSection tenantId="tenant-a" tenantName="Acme A.Ş." />)
    expect(await screen.findByRole('status')).toHaveTextContent('Yükleniyor')
    pending.unmount()
    vi.mocked(api.getSessions).mockRejectedValue(new ApiError(500))
    renderPlatform(<SupportAccessSection tenantId="tenant-a" tenantName="Acme A.Ş." />)
    expect(await screen.findByRole('alert')).toHaveTextContent('Sunucu isteği tamamlayamadı')
    expect(screen.getByRole('button', { name: 'Destek oturumu aç' })).toBeEnabled()
  })

  it('renders the empty recent-session state', async () => {
    vi.mocked(api.getSessions).mockResolvedValue(paged([], { pageSize: 5 }))
    renderPlatform(<SupportAccessSection tenantId="tenant-a" tenantName="Acme A.Ş." />)
    await waitFor(() => expect(api.getSessions).toHaveBeenCalledOnce())
    await waitFor(() => expect(screen.queryByRole('status')).not.toBeInTheDocument())
    expect(screen.getByRole('link', { name: 'Tüm destek oturumları' })).toBeInTheDocument()
    expect(screen.getByText('Destek oturumu bulunamadı.')).toBeInTheDocument()
  })
})
