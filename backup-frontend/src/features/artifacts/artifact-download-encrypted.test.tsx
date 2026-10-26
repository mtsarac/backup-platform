import { cleanup, fireEvent, screen, waitFor } from '@testing-library/react'
import { render } from '@/test/render'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ArtifactDownload } from './artifact-download'
import { getDownloadAvailability, getDownloadStatus, prepareDownload } from './api'
import type { DownloadAvailability } from './api'

vi.mock('./api', () => ({ getDownloadAvailability: vi.fn(), getDownloadStatus: vi.fn(), prepareDownload: vi.fn() }))
const availability: DownloadAvailability = { available: true, reason: null, checksum: 'cipher-sha256', sizeBytes: 4096, format: 'v2-bpe', replicas: [
  { replicaId: 'central', storageProvider: 'Central', storageTarget: 'Managed storage', available: true, reason: null },
] }
const url = '/api/tenants/tenant-a/backup-artifacts/artifact-a/downloads/session-a/content'
function mount(canAdmin = true) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(<QueryClientProvider client={client}><ArtifactDownload tenantId="tenant-a" artifactId="artifact-a" canAdmin={canAdmin} eligible /></QueryClientProvider>)
}
beforeEach(() => {
  vi.mocked(getDownloadAvailability).mockResolvedValue(availability)
  vi.mocked(getDownloadStatus).mockResolvedValue({ sessionId: 'session-a', state: 'Ready', transferredBytes: 0 })
})
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.clearAllMocks() })

describe('download of an encrypted Backup', () => {
  it('defaults to the stored .bpe file and never sends the decrypt flag', async () => {
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {})
    vi.mocked(prepareDownload).mockResolvedValue({ sessionId: 'session-a', downloadUrl: url, filename: 'backup-1.bak.bpe', checksum: 'cipher-sha256', sizeBytes: 4096, decrypted: false })
    mount()
    expect(await screen.findByRole('radio', { name: /Şifreli dosya \(\.bpe\)/ })).toBeChecked()
    expect(screen.getByText('Şifreli dosyanın SHA256 checksum değeri')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Yedeklemeyi İndir' }))
    await waitFor(() => expect(click).toHaveBeenCalledOnce())
    expect(prepareDownload).toHaveBeenCalledWith('tenant-a', 'artifact-a', undefined)
    expect((click.mock.instances[0] as HTMLAnchorElement).download).toBe('backup-1.bak.bpe')
  })

  it('requests a decrypted download on explicit choice and shows the plaintext checksum', async () => {
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {})
    vi.mocked(prepareDownload).mockResolvedValue({ sessionId: 'session-a', downloadUrl: url, filename: 'backup-1.bak', checksum: 'plain-sha256', sizeBytes: 4096, decrypted: true })
    mount()
    fireEvent.click(await screen.findByRole('radio', { name: /Çözülmüş dosya/ }))
    fireEvent.click(screen.getByRole('button', { name: 'Çözülmüş dosyayı indir' }))
    await waitFor(() => expect(click).toHaveBeenCalledOnce())
    expect(prepareDownload).toHaveBeenCalledWith('tenant-a', 'artifact-a', undefined, true)
    expect(await screen.findByText('plain-sha256')).toBeInTheDocument()
    expect((click.mock.instances[0] as HTMLAnchorElement).download).toBe('backup-1.bak')
  })

  it('offers no choice for a plain artifact', async () => {
    vi.mocked(getDownloadAvailability).mockResolvedValue({ ...availability, format: 'v1-plain' })
    mount()
    await screen.findByRole('button', { name: 'Yedeklemeyi İndir' })
    expect(screen.queryByRole('radio')).not.toBeInTheDocument()
  })

  it('shows the key_unavailable reason when decryption cannot start', async () => {
    const { ApiError } = await vi.importActual<typeof import('@/lib/api/client')>('@/lib/api/client')
    vi.mocked(prepareDownload).mockRejectedValue(new ApiError(409, { error: 'key_unavailable' }))
    mount()
    fireEvent.click(await screen.findByRole('radio', { name: /Çözülmüş dosya/ }))
    fireEvent.click(screen.getByRole('button', { name: 'Çözülmüş dosyayı indir' }))
    expect(await screen.findByText(/Şifreleme anahtarı kullanılamıyor/)).toBeInTheDocument()
  })

  it('keeps the whole download Owner / Admin only', async () => {
    mount(false)
    expect(screen.getByRole('status')).toBeInTheDocument()
    expect(screen.queryByRole('radio')).not.toBeInTheDocument()
    expect(getDownloadAvailability).not.toHaveBeenCalled()
  })
})
