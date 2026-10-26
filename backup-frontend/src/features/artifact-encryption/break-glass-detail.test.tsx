import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { renderPlatform } from '@/features/platform/test-utils'
import * as api from './api'
import { BreakGlassDetail } from './break-glass-detail'
import { makeBreakGlass } from './test-fixtures'
import type { BreakGlassRequest } from './types'

vi.mock('./api')
beforeEach(() => vi.resetAllMocks())
afterEach(() => vi.restoreAllMocks())
const mount = (request: BreakGlassRequest, receivedAt = Date.now()) => renderPlatform(<BreakGlassDetail request={request} receivedAt={receivedAt} />)

describe('break-glass dual control', () => {
  it('never offers the requester an approve or deny action on their own request', () => {
    mount(makeBreakGlass({ isRequester: true, canApprove: false }))
    expect(screen.queryByRole('button', { name: 'Onayla' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Reddet' })).not.toBeInTheDocument()
    expect(screen.getByText(/farklı bir PlatformAdmin onaylamalıdır/)).toBeInTheDocument()
  })

  it('never lets the requester approve or deny even when the server flag is wrong', () => {
    mount(makeBreakGlass({ isRequester: true, canApprove: true }))
    expect(screen.queryByRole('button', { name: 'Onayla' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Reddet' })).not.toBeInTheDocument()
  })

  it('lets a different admin approve only after typing the confirmation name, and shows the countdown from the server clock', async () => {
    vi.mocked(api.approveBreakGlass).mockResolvedValue(makeBreakGlass({ status: 'Approved', canApprove: false }))
    mount(makeBreakGlass())
    expect(screen.getByText('0 sa 55 dk 0 sn')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Onayla' }))
    expect(api.approveBreakGlass).not.toHaveBeenCalled()
    const dialog = await screen.findByRole('dialog')
    const confirm = within(dialog).getByRole('button', { name: 'Onayla' })
    const input = within(dialog).getByRole('textbox', { name: /Acme/ })
    expect(confirm).toBeDisabled()
    for (const wrong of ['acme', 'Acme ', 'Acm']) { fireEvent.change(input, { target: { value: wrong } }); expect(confirm).toBeDisabled() }
    fireEvent.click(confirm)
    expect(api.approveBreakGlass).not.toHaveBeenCalled()
    fireEvent.change(input, { target: { value: 'Acme' } })
    fireEvent.click(confirm)
    await waitFor(() => expect(api.approveBreakGlass).toHaveBeenCalledWith('request-a'))
  })

  it('keeps ids and raw status under collapsed copyable technical details, out of the main view', () => {
    mount(makeBreakGlass())
    const details = screen.getByText('Teknik ayrıntılar').closest('details') as HTMLDetailsElement
    expect(details.open).toBe(false)
    expect(within(details).getByText('11111111-1111-1111-1111-111111111111')).toBeInTheDocument()
    expect(within(details).getByText('request-a')).toBeInTheDocument()
    expect(within(details).getAllByRole('button', { name: /^Kopyala/ }).length).toBeGreaterThan(1)
    expect(screen.getAllByText('11111111-1111-1111-1111-111111111111')).toHaveLength(1)
  })

  it('requires a reason to deny and sends it', async () => {
    vi.mocked(api.denyBreakGlass).mockResolvedValue(makeBreakGlass({ status: 'Denied', canApprove: false }))
    mount(makeBreakGlass())
    fireEvent.click(screen.getByRole('button', { name: 'Reddet' }))
    const dialog = await screen.findByRole('dialog')
    fireEvent.click(within(dialog).getByRole('button', { name: 'Reddet' }))
    expect(await within(dialog).findByText('Ret gerekçesi girin.')).toBeInTheDocument()
    expect(api.denyBreakGlass).not.toHaveBeenCalled()
    fireEvent.change(within(dialog).getByLabelText('Ret gerekçesi'), { target: { value: 'no ticket' } })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Reddet' }))
    await waitFor(() => expect(api.denyBreakGlass).toHaveBeenCalledWith('request-a', 'no ticket'))
  })

  it('offers the download only to the requester of an approved request, as a native navigation', async () => {
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {})
    vi.mocked(api.downloadBreakGlass).mockResolvedValue({ sessionId: 's1', downloadUrl: '/api/platform/artifact-break-glass/request-a/downloads/s1/content', filename: 'backup.bak', checksum: 'plain-sha', sizeBytes: 10, decrypted: true })
    vi.mocked(api.getBreakGlassDownloadStatus).mockResolvedValue({ sessionId: 's1', state: 'Ready', transferredBytes: 0 })
    mount(makeBreakGlass({ status: 'Approved', isRequester: true, canApprove: false, canDownload: true, decidedByName: 'Bo Admin', decidedAt: '2026-10-19T10:06:00Z' }))
    fireEvent.click(screen.getByRole('button', { name: 'Çözülmüş dosyayı indir' }))
    await waitFor(() => expect(click).toHaveBeenCalledOnce())
    expect(api.downloadBreakGlass).toHaveBeenCalledWith('request-a')
    const anchor = click.mock.instances[0] as HTMLAnchorElement
    expect(anchor.download).toBe('backup.bak')
    expect(anchor.href).toContain('/api/platform/artifact-break-glass/request-a/downloads/s1/content')
  })

  it('warns before the download that starting it spends the single-use grant, then offers it no more', async () => {
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {})
    vi.mocked(api.downloadBreakGlass).mockResolvedValue({ sessionId: 's1', downloadUrl: '/api/platform/artifact-break-glass/request-a/downloads/s1/content', filename: 'backup.bak', checksum: 'plain-sha', sizeBytes: 10, decrypted: true })
    vi.mocked(api.getBreakGlassDownloadStatus).mockResolvedValue({ sessionId: 's1', state: 'Ready', transferredBytes: 0 })
    mount(makeBreakGlass({ status: 'Approved', isRequester: true, canApprove: false, canDownload: true }))
    expect(screen.getByText('Tek kullanımlık çözülmüş indirme')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Çözülmüş dosyayı indir' }))
    await waitFor(() => expect(api.downloadBreakGlass).toHaveBeenCalledOnce())
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Çözülmüş dosyayı indir' })).not.toBeInTheDocument())
    expect(screen.queryByText('Tek kullanımlık çözülmüş indirme')).not.toBeInTheDocument()
  })

  it('never navigates an unsafe download URL', async () => {
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {})
    vi.mocked(api.downloadBreakGlass).mockResolvedValue({ sessionId: 's1', downloadUrl: 'https://evil.example/x?token=1', filename: 'backup.bak', checksum: 'x', sizeBytes: 10, decrypted: true })
    vi.mocked(api.getBreakGlassDownloadStatus).mockResolvedValue({ sessionId: 's1', state: 'Ready', transferredBytes: 0 })
    mount(makeBreakGlass({ status: 'Approved', isRequester: true, canApprove: false, canDownload: true }))
    fireEvent.click(screen.getByRole('button', { name: 'Çözülmüş dosyayı indir' }))
    expect(await screen.findByText('İndirme bağlantısı güvenli değil.')).toBeInTheDocument()
    expect(click).not.toHaveBeenCalled()
  })

  it('does not let the approver, or anyone after use, download', () => {
    const { unmount } = mount(makeBreakGlass({ status: 'Approved', isRequester: false, canApprove: false, canDownload: false }))
    expect(screen.queryByRole('button', { name: 'Çözülmüş dosyayı indir' })).not.toBeInTheDocument()
    expect(screen.getByText(/Yalnızca isteği açan yönetici/)).toBeInTheDocument()
    unmount()
    mount(makeBreakGlass({ status: 'Consumed', isRequester: true, canApprove: false, canDownload: false, consumedAt: '2026-10-19T10:30:00Z' }))
    expect(screen.queryByRole('button', { name: 'Çözülmüş dosyayı indir' })).not.toBeInTheDocument()
    expect(screen.getByText(/Tek kullanımlıktır; yeniden indirmek için yeni bir istek açın/)).toBeInTheDocument()
  })

  it('stops offering actions once the server clock says the request expired', () => {
    mount(makeBreakGlass({ serverNow: '2026-10-19T11:00:01Z' }))
    expect(screen.queryByRole('button', { name: 'Onayla' })).not.toBeInTheDocument()
    expect(screen.getByText(/İsteğin süresi doldu/)).toBeInTheDocument()
  })
})
