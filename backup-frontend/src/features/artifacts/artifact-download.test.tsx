import { cleanup, fireEvent, screen, waitFor } from '@testing-library/react'
import { render } from '@/test/render'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ArtifactDownload } from './artifact-download'
import { getDownloadAvailability, getDownloadStatus, prepareDownload } from './api'
import type { DownloadAvailability, DownloadStatus } from './api'

vi.mock('./api', () => ({ getDownloadAvailability: vi.fn(), getDownloadStatus: vi.fn(), prepareDownload: vi.fn() }))
const availability: DownloadAvailability = { available: true, reason: null, checksum: 'expected-sha256', sizeBytes: 4096, format: 'v1-plain', replicas: [
  { replicaId: 'central', storageProvider: 'Central', storageTarget: 'Managed storage', available: true, reason: null },
  { replicaId: 'local', storageProvider: 'Local', storageTarget: 'Agent storage', available: false, reason: 'agent_unavailable' },
] }
function mount(tenantId = 'tenant-a', canAdmin = true, eligible = true) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  const view = render(<QueryClientProvider client={client}><ArtifactDownload tenantId={tenantId} artifactId="artifact-a" canAdmin={canAdmin} eligible={eligible} /></QueryClientProvider>)
  return { client, ...view }
}
beforeEach(() => {
  vi.mocked(getDownloadAvailability).mockResolvedValue(availability)
  vi.mocked(prepareDownload).mockResolvedValue({ sessionId: 'session-a', downloadUrl: '/api/tenants/tenant-a/backup-artifacts/artifact-a/downloads/session-a/content', filename: 'backup.bak', checksum: availability.checksum, sizeBytes: 4096, decrypted: false })
  vi.mocked(getDownloadStatus).mockResolvedValue({ sessionId: 'session-a', state: 'Ready', transferredBytes: 0 })
})
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.clearAllMocks(); vi.unstubAllGlobals() })
describe('secure Backup download', () => {
  it('shows availability loading and Central/Local destinations, including offline Agent', async () => {
    mount()
    expect(screen.getByRole('status')).toHaveTextContent('kontrol ediliyor')
    expect(await screen.findByRole('option', { name: 'Central · Managed storage' })).toBeEnabled()
    expect(screen.getByRole('option', { name: /Local · Agent storage/ })).toBeDisabled()
    expect(screen.getByRole('option', { name: /Local · Agent storage/ })).toHaveTextContent('Yedekleme bilgisayarı kullanılamıyor')
  })
  it('prepares selected replica and starts a native anchor download without fetching file bytes', async () => {
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {})
    mount()
    fireEvent.change(await screen.findByRole('combobox', { name: 'İndirme hedefi' }), { target: { value: 'central' } })
    fireEvent.click(screen.getByRole('button', { name: "Yedeklemeyi İndir" }))
    await screen.findByText(/İndirme başlatıldı/)
    expect(prepareDownload).toHaveBeenCalledWith('tenant-a', 'artifact-a', 'central')
    await waitFor(() => expect(click).toHaveBeenCalledOnce())
    const anchor = click.mock.instances[0] as HTMLAnchorElement
    expect(anchor.download).toBe('backup.bak')
    expect(anchor.href).toContain('/api/tenants/tenant-a/backup-artifacts/artifact-a/downloads/session-a/content')
  })
  it('leaves automatic replica selection to the server and permits retry after exhausted fallback', async () => {
    vi.mocked(getDownloadAvailability).mockResolvedValue({ ...availability, replicas: [
      availability.replicas[0],
      { replicaId: 'sftp', storageProvider: 'SFTP', storageTarget: 'Remote storage', available: true, reason: null },
      availability.replicas[1],
    ] })
    vi.mocked(getDownloadStatus).mockResolvedValue({ sessionId: 'session-a', state: 'Failed', failureCode: 'storage_unavailable', transferredBytes: 0 })
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {})
    mount()
    const selector = await screen.findByRole('combobox', { name: 'İndirme hedefi' })
    expect(selector).toHaveValue('')
    fireEvent.click(screen.getByRole('button', { name: "Yedeklemeyi İndir" }))
    expect(await screen.findByRole('alert')).toBeInTheDocument()
    expect(prepareDownload).toHaveBeenCalledWith('tenant-a', 'artifact-a', undefined)
    expect(click).not.toHaveBeenCalled()
    expect(selector).toBeEnabled()
    expect(screen.getByRole('button', { name: "Yedeklemeyi İndir" })).toBeEnabled()
    fireEvent.change(selector, { target: { value: 'sftp' } })
    fireEvent.click(screen.getByRole('button', { name: "Yedeklemeyi İndir" }))
    await waitFor(() => expect(prepareDownload).toHaveBeenLastCalledWith('tenant-a', 'artifact-a', 'sftp'))
    expect(click).not.toHaveBeenCalled()
  })
  it('keeps Preparing explicit and displays safe terminal integrity failure', async () => {
    vi.mocked(getDownloadStatus).mockResolvedValue({ sessionId: 'session-a', state: 'Preparing', transferredBytes: 0 })
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {})
    const { client } = mount()
    fireEvent.click(await screen.findByRole('button', { name: "Yedeklemeyi İndir" }))
    await screen.findByRole('button', { name: 'İndirme hazırlanıyor…' })
    expect(click).not.toHaveBeenCalled()
    vi.mocked(getDownloadStatus).mockResolvedValue({ sessionId: 'session-a', state: 'Failed', failureCode: 'integrity_mismatch', transferredBytes: 0 })
    await client.invalidateQueries({ queryKey: ['tenant', 'tenant-a', 'backup-artifacts', 'download-status'] })
    expect(await screen.findByRole('alert')).toHaveTextContent('sağlama toplamı doğrulaması başarısız')
    expect(click).not.toHaveBeenCalled()
  })
  it('denies non-admin and deletion-pending artifacts without requesting availability', () => {
    mount('tenant-a', false)
    expect(screen.getByText(/yalnızca Sahip/)).toBeInTheDocument()
    expect(getDownloadAvailability).not.toHaveBeenCalled()
    cleanup()
    mount('tenant-a', true, false)
    expect(screen.getByRole('status')).toHaveTextContent('siliniyor veya silinmiş')
    expect(getDownloadAvailability).not.toHaveBeenCalled()
  })
  it('disables unavailable downloads and safely maps prepare errors', async () => {
    vi.mocked(getDownloadAvailability).mockResolvedValue({ ...availability, available: false, reason: 'agent_unavailable' })
    mount()
    expect(await screen.findByRole('button', { name: "Yedeklemeyi İndir" })).toBeDisabled()
    expect(screen.getByText(/İndirme kullanılamıyor/)).toHaveTextContent('Yedekleme bilgisayarı kullanılamıyor')
    cleanup()
    vi.mocked(getDownloadAvailability).mockResolvedValue(availability)
    vi.mocked(prepareDownload).mockRejectedValue(new Error('secret storage path'))
    mount()
    fireEvent.click(await screen.findByRole('button', { name: "Yedeklemeyi İndir" }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Sunucuya ulaşılamadı')
    expect(screen.queryByText(/secret storage path/)).not.toBeInTheDocument()
  })
  it('copies expected checksum and isolates availability by tenant in shared cache', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined)
    vi.stubGlobal('navigator', { ...navigator, clipboard: { writeText } })
    const { client, rerender } = mount()
    fireEvent.click(await screen.findByRole('button', { name: "Sağlama toplamını kopyala" }))
    await screen.findByText("Sağlama toplamı kopyalandı.")
    expect(writeText).toHaveBeenCalledWith('expected-sha256')
    vi.mocked(getDownloadAvailability).mockResolvedValue({ ...availability, checksum: 'tenant-b-checksum' })
    rerender(<QueryClientProvider client={client}><ArtifactDownload tenantId="tenant-b" artifactId="artifact-a" canAdmin eligible /></QueryClientProvider>)
    expect(screen.queryByText('expected-sha256')).not.toBeInTheDocument()
    await screen.findByText('tenant-b-checksum')
    expect(getDownloadAvailability).toHaveBeenLastCalledWith('tenant-b', 'artifact-a', expect.any(AbortSignal))
    vi.unstubAllGlobals()
  })
  it('does not start a pending download after permissions are revoked', async () => {
    const deferred = Promise.withResolvers<DownloadStatus>()
    vi.mocked(getDownloadStatus).mockReturnValue(deferred.promise)
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {})
    const { client, rerender } = mount()
    fireEvent.click(await screen.findByRole('button', { name: "Yedeklemeyi İndir" }))
    await waitFor(() => expect(getDownloadStatus).toHaveBeenCalled())
    rerender(<QueryClientProvider client={client}><ArtifactDownload tenantId="tenant-a" artifactId="artifact-a" canAdmin={false} eligible /></QueryClientProvider>)
    deferred.resolve({ sessionId: 'session-a', state: 'Ready', transferredBytes: 0 })
    await screen.findByText(/yalnızca Sahip/)
    expect(click).not.toHaveBeenCalled()
  })
})
