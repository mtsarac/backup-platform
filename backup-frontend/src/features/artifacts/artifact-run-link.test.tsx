import { screen } from '@testing-library/react'
import { render } from '@/test/render'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ArtifactRunLink } from './artifact-run-link'
import * as ArtifactApi from './api'
import type { BackupArtifact } from './types'

vi.mock('./api', () => ({ getArtifact: vi.fn() }))

function renderLink() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  render(<QueryClientProvider client={client}><MemoryRouter><ArtifactRunLink tenantId="tenant-a" artifactId="artifact-1">SalesDB</ArtifactRunLink></MemoryRouter></QueryClientProvider>)
}

describe('ArtifactRunLink', () => {
  beforeEach(() => vi.clearAllMocks())

  it('sends a backup file reference to the backup run that produced it', async () => {
    vi.mocked(ArtifactApi.getArtifact).mockResolvedValue({ id: 'artifact-1', runId: 'run-1', replicas: [] } as unknown as BackupArtifact)
    renderLink()
    expect(await screen.findByRole('link', { name: 'SalesDB' })).toHaveAttribute('href', '/tenants/tenant-a/backup-runs/run-1')
  })

  it('shows plain text, not a dead link, while the run is unknown', () => {
    vi.mocked(ArtifactApi.getArtifact).mockReturnValue(new Promise(() => {}))
    renderLink()
    expect(screen.getByText('SalesDB')).toBeInTheDocument()
    expect(screen.queryByRole('link')).not.toBeInTheDocument()
  })
})
