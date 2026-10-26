import { cleanup, fireEvent, screen, waitFor } from '@testing-library/react'
import { render } from '@/test/render'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { DownloadStatus, PreparedDownload } from './api'
import { DownloadFlowStatus } from './download-flow-status'
import { useDownloadFlow } from './use-download-flow'

const TOKEN = 'tok-9f3a'
const url = `/api/tenants/t/backup-artifacts/a/downloads/s1/${TOKEN}/content`
const prepared: PreparedDownload = { sessionId: 's1', downloadUrl: url, filename: 'backup.bak', checksum: 'sha', sizeBytes: 10, decrypted: false }
const prepare = vi.fn<() => Promise<PreparedDownload>>()
const fetchStatus = vi.fn<(sessionId: string, signal: AbortSignal) => Promise<DownloadStatus>>()
let client: QueryClient

function Harness({ active = true }: { active?: boolean }) {
  const flow = useDownloadFlow({ active, prepare, fetchStatus, statusKey: id => ['download-status', id] })
  return <div>
    <button disabled={flow.busy} onClick={flow.start}>başlat</button>
    <output data-testid="session">{JSON.stringify(flow.session ?? null)}</output>
    <DownloadFlowStatus flow={flow} />
  </div>
}
function mount(active = true) {
  client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(<QueryClientProvider client={client}><Harness active={active} /></QueryClientProvider>)
}
beforeEach(() => {
  prepare.mockResolvedValue(prepared)
  fetchStatus.mockResolvedValue({ sessionId: 's1', state: 'Ready', transferredBytes: 0 })
})
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.clearAllMocks() })

describe('download flow', () => {
  it('starts the browser download once the transfer is ready', async () => {
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {})
    mount()
    fireEvent.click(screen.getByRole('button', { name: 'başlat' }))
    await waitFor(() => expect(click).toHaveBeenCalledOnce())
    const anchor = click.mock.instances[0] as HTMLAnchorElement
    expect(anchor.download).toBe('backup.bak')
    expect(anchor.href).toContain(url)
  })
  it('keeps no download URL or token in page state once the download has started', async () => {
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {})
    const { container } = mount()
    fireEvent.click(screen.getByRole('button', { name: 'başlat' }))
    await waitFor(() => expect(screen.getByTestId('session')).toHaveTextContent('backup.bak'))
    await waitFor(() => expect(screen.getByTestId('session').textContent).not.toContain(TOKEN))
    expect(container.innerHTML).not.toContain(TOKEN)
    expect(JSON.stringify(client.getMutationCache().getAll().map(mutation => mutation.state))).not.toContain(TOKEN)
    expect(JSON.stringify(client.getQueryCache().getAll().map(query => query.state.data))).not.toContain(TOKEN)
  })
  it('never navigates an unsafe URL and does not keep it', async () => {
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {})
    prepare.mockResolvedValue({ ...prepared, downloadUrl: `https://evil.example/x?token=${TOKEN}` })
    mount()
    fireEvent.click(screen.getByRole('button', { name: 'başlat' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('güvenli değil')
    expect(click).not.toHaveBeenCalled()
    expect(screen.getByTestId('session').textContent).not.toContain(TOKEN)
    expect(fetchStatus).not.toHaveBeenCalled()
  })
  it('ignores a second start while one is in progress', async () => {
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {})
    const gate = Promise.withResolvers<PreparedDownload>()
    prepare.mockReturnValue(gate.promise)
    mount()
    const button = screen.getByRole('button', { name: 'başlat' })
    fireEvent.click(button); fireEvent.click(button)
    gate.resolve(prepared)
    await waitFor(() => expect(fetchStatus).toHaveBeenCalled())
    expect(prepare).toHaveBeenCalledOnce()
  })
  it('does not start a pending download after permission is revoked', async () => {
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {})
    const gate = Promise.withResolvers<DownloadStatus>()
    fetchStatus.mockReturnValue(gate.promise)
    const view = mount()
    fireEvent.click(screen.getByRole('button', { name: 'başlat' }))
    await waitFor(() => expect(fetchStatus).toHaveBeenCalled())
    view.rerender(<QueryClientProvider client={client}><Harness active={false} /></QueryClientProvider>)
    gate.resolve({ sessionId: 's1', state: 'Ready', transferredBytes: 0 })
    await new Promise(resolve => setTimeout(resolve, 20))
    expect(click).not.toHaveBeenCalled()
  })
  it('shows a failed transfer as an error with its next step', async () => {
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {})
    fetchStatus.mockResolvedValue({ sessionId: 's1', state: 'Failed', failureCode: 'storage_unavailable', transferredBytes: 0 })
    mount()
    fireEvent.click(screen.getByRole('button', { name: 'başlat' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Ne yapmalı')
    expect(click).not.toHaveBeenCalled()
  })
})
