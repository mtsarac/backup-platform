import { fireEvent, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { renderPlatform } from '@/features/platform/test-utils'
import * as api from './api'
import { SupportArtifactDownload } from './artifact-download'
import { supportKeys } from './hooks'
import type { SupportScope } from './types'

vi.mock('./api')
const scope: SupportScope = { sessionId: 'support-a', tenantId: 'tenant-a', enabled: true }
const url = '/api/platform/support-sessions/support-a/artifacts/artifact-a/downloads/download-a/content'
beforeEach(() => {
  vi.resetAllMocks()
  vi.mocked(api.getSupportDownloadAvailability).mockResolvedValue({ available: true, reason: null, checksum: 'expected-sha', sizeBytes: 4096, replicas: [{ replicaId: 'replica-a', storageProvider: 'Central', available: true, reason: null }, { replicaId: 'offline', storageProvider: 'Local', available: false, reason: 'agent_unavailable' }] })
  vi.mocked(api.prepareSupportDownload).mockResolvedValue({ sessionId: 'download-a', downloadUrl: url, filename: 'backup.bak', checksum: 'expected-sha', sizeBytes: 4096 })
  vi.mocked(api.getSupportDownloadStatus).mockResolvedValue({ sessionId: 'download-a', state: 'Ready', transferredBytes: 0 })
})
afterEach(() => vi.restoreAllMocks())
describe('support native download', () => {
  it('prepares a selected Replica then navigates exactly to the relative downloadUrl, without fetching content', async () => {
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {})
    const { client } = renderPlatform(<SupportArtifactDownload scope={scope} artifactId="artifact-a" eligible />)
    fireEvent.change(await screen.findByRole('combobox', { name: "Kopya" }), { target: { value: 'replica-a' } })
    expect(screen.getByRole('option', { name: /Yerel/ })).toBeDisabled()
    fireEvent.click(screen.getByRole('button', { name: "İndir" }))
    await waitFor(() => expect(click).toHaveBeenCalledOnce())
    expect(api.prepareSupportDownload).toHaveBeenCalledWith('support-a', 'artifact-a', 'replica-a')
    const anchor = click.mock.instances[0] as HTMLAnchorElement
    expect(anchor.getAttribute('href')).toBe(url)
    expect(anchor.download).toBe('backup.bak')
    expect(api.getSupportDownloadStatus).toHaveBeenCalledWith('support-a', 'artifact-a', 'download-a', expect.any(AbortSignal))
    const keys = client.getQueryCache().getAll().map(query => query.queryKey)
    expect(keys).toContainEqual(supportKeys.data(scope, 'downloads', 'status', 'artifact-a', 'download-a'))
  })
  it('shows safe availability failure and never prepares unavailable backups', async () => {
    vi.mocked(api.getSupportDownloadAvailability).mockResolvedValue({ available: false, reason: 'replica_unavailable', checksum: 'sha', sizeBytes: 1, replicas: [] })
    renderPlatform(<SupportArtifactDownload scope={scope} artifactId="artifact-a" eligible />)
    expect(await screen.findByText(/Kopya şu anda indirme/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: "İndir" })).toBeDisabled()
    fireEvent.click(screen.getByRole('button', { name: "İndir" }))
    expect(api.prepareSupportDownload).not.toHaveBeenCalled()
  })
  it('disabled access makes no availability/status requests or native navigation', () => {
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {})
    renderPlatform(<SupportArtifactDownload scope={{ ...scope, enabled: false }} artifactId="artifact-a" eligible />)
    expect(screen.getByRole('button', { name: "İndir" })).toBeDisabled()
    expect(api.getSupportDownloadAvailability).not.toHaveBeenCalled()
    expect(api.getSupportDownloadStatus).not.toHaveBeenCalled()
    expect(click).not.toHaveBeenCalled()
  })
  it('never navigates an unsafe returned URL', async () => {
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {})
    vi.mocked(api.prepareSupportDownload).mockResolvedValue({ sessionId: 'download-a', downloadUrl: 'https://evil.example/content?token=secret', filename: 'backup.bak', checksum: 'sha', sizeBytes: 4096 })
    renderPlatform(<SupportArtifactDownload scope={scope} artifactId="artifact-a" eligible />)
    await screen.findByRole('combobox', { name: "Kopya" })
    fireEvent.click(screen.getByRole('button', { name: "İndir" }))
    expect(await screen.findByRole('alert')).toHaveTextContent('bağlantısı güvenli değil')
    expect(click).not.toHaveBeenCalled()
  })
})
