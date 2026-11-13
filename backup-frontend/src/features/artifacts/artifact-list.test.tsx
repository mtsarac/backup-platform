import { cleanup, render as rtlRender, screen, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, describe, expect, it } from 'vitest'
import { artifactAvailability } from './availability'
import { ArtifactAvailability } from './artifact-availability'
import { artifactSourceLabel, artifactSourceScope } from './source'
import { backupOption } from './backup-option'
import type { BackupArtifact, BackupReplica } from './types'
import { ArtifactsPage } from '@/pages/artifacts/artifacts-page'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { Route, Routes } from 'react-router-dom'
import { vi } from 'vitest'
import { apiRequest } from '@/lib/api/client'

vi.mock('@/lib/api/client', () => ({
  apiRequest: vi.fn(), API_BASE_URL: '/api', tenantPath: (t: string, p: string) => `tenants/${t}/${p}`, ApiError: class ApiError extends Error {},
}))
vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => ({ tenantId: 'tenant-a', canAdmin: true, canOperate: true }) }))
vi.mock('@/hooks/use-large-screen', () => ({ useLargeScreen: () => large.value }))
const large = vi.hoisted(() => ({ value: true }))

const replica: BackupReplica = {
  id: 'r1', storageProvider: 'Local', storageConnectionId: 'c', storageTarget: 't', key: 'k', sizeBytes: 1, checksum: 'x', checksumAlgorithm: 'SHA256',
  verificationState: 'Verified', verificationMethod: 'ReadBack', createdAt: '2026-10-05T10:00:00Z', verifiedAt: null, status: 'Available', usable: true,
  deleteRequestedAt: null, deleteAfter: null, deleteReason: null, deletedAt: null, deleteAttempts: 0, deleteFailureCode: null, deleteNextAttemptAt: null, deletionExecutor: 'Agent', waitingForAgent: false,
}
const meta = (engine: string) => ({ engine }) as never
function artifact(o: Partial<BackupArtifact> = {}): BackupArtifact {
  return {
    id: 'a1', sizeBytes: 5_000_000, sourceChecksum: 's', artifactChecksum: 'a', checksumAlgorithm: 'SHA256', createdAt: '2026-10-05T10:00:00Z', acceptedAt: null,
    retentionExpiresAt: null, retentionPinnedAt: null, state: 'Available', deleteAfter: null, deleteReason: null, runId: 'run', jobId: 'job', attemptId: null,
    jobName: 'Gece', databaseName: 'Accounting', agentName: 'SRV-SQL-01', backupType: 'Full', backupMetadata: meta('mssql'), replicas: [{ ...replica }], ...o,
  }
}
afterEach(() => { cleanup(); vi.clearAllMocks(); large.value = true })

describe('artifactSourceScope', () => {
  it.each([[['reports', 'docs'], 'reports, docs'], [['.'], 'Tüm kaynak'], [[], null], [null, null], [undefined, null]])('%j -> %j', (sourcePaths, expected) => {
    expect(artifactSourceScope({ sourcePaths })).toBe(expected)
  })
})

describe('artifactAvailability', () => {
  const cases: [string, Partial<BackupArtifact>, string, string][] = [
    ['verified usable copy', {}, 'safe', 'Geri yüklemeye hazır'],
    ['copy still being verified', { replicas: [{ ...replica, verificationState: 'Pending' }] }, 'running', 'Doğrulanıyor'],
    ['failed verification', { replicas: [{ ...replica, verificationState: 'Failed' }] }, 'attention', 'Uygun değil'],
    ['verified but unusable', { replicas: [{ ...replica, usable: false }] }, 'attention', 'Uygun değil'],
    ['no replicas', { replicas: [] }, 'attention', 'Uygun değil'],
    ['differential backup', { backupType: 'Differential' }, 'attention', 'Uygun değil'],
    ['deletion pending', { state: 'DeletePending', replicas: [{ ...replica, status: 'DeletePending' }] }, 'attention', 'Siliniyor'],
    ['deletion failed', { replicas: [{ ...replica, status: 'DeleteFailed' }] }, 'urgent', 'Silme başarısız'],
    ['deleted', { state: 'Deleted', replicas: [{ ...replica, status: 'Deleted', usable: false }] }, 'neutral', 'Silindi'],
  ]
  it.each(cases)('%s', (_name, overrides, level, label) => {
    expect(artifactAvailability(artifact(overrides))).toMatchObject({ level, label })
  })
  it('never calls an unverified file ready, and says why', () => {
    const { label, reason } = artifactAvailability(artifact({ replicas: [{ ...replica, verificationState: 'Failed' }] }))
    expect(label).not.toBe('Geri yüklemeye hazır')
    expect(reason).toMatch(/doğrulanmadığı/)
  })
  it('keeps a degraded replica policy as supporting text on a ready file', () => {
    const replication = { status: 'Degraded', requiredVerifiedReplicas: 2, verifiedReplicas: 1, destinationCount: 2, policySatisfied: false, destinations: [] } satisfies BackupArtifact['replication']
    expect(artifactAvailability(artifact({ replication }))).toMatchObject({ level: 'safe', label: 'Geri yüklemeye hazır', reason: 'Kopya sayısı politikanın gerisinde.' })
  })
  it('renders icon-independent text', () => {
    rtlRender(<ArtifactAvailability artifact={artifact()} />)
    expect(screen.getByText('Geri yüklemeye hazır')).toBeVisible()
  })
})

describe('source label', () => {
  it('names type, source and computer for each engine', () => {
    expect(artifactSourceLabel(artifact())).toBe('MSSQL · Accounting · SRV-SQL-01')
    expect(artifactSourceLabel(artifact({ backupMetadata: meta('postgres') }))).toBe('PostgreSQL · Accounting · SRV-SQL-01')
    expect(artifactSourceLabel(artifact({ backupMetadata: meta('file-system'), databaseName: 'samplefiles' }))).toBe('Dosya yedeği · samplefiles · SRV-SQL-01')
  })
  it('leaves missing fields out instead of faking them', () => {
    expect(artifactSourceLabel(artifact({ backupMetadata: null, agentName: null }))).toBe('Accounting')
    expect(artifactSourceLabel(artifact({ backupMetadata: null, agentName: null, databaseName: null }))).toBe('')
  })
  it('puts type and computer in the picker description', () => {
    expect(backupOption(artifact({ backupMetadata: meta('file-system') })).description).toBe('Dosya yedeği · SRV-SQL-01 · Gece')
  })
})

function mount(items: BackupArtifact[] | 'pending' | Error, wide = true) {
  large.value = wide
  const mock = vi.mocked(apiRequest)
  if (items === 'pending') mock.mockReturnValue(new Promise(() => {}))
  else if (items instanceof Error) mock.mockRejectedValue(items)
  else mock.mockResolvedValue({ items, totalCount: items.length, page: 1, pageSize: 25 })
  rtlRender(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}><MemoryRouter initialEntries={['/tenants/tenant-a/artifacts']}>
    <Routes><Route path="/tenants/:tenantId/artifacts" element={<ArtifactsPage />} /></Routes></MemoryRouter></QueryClientProvider>)
}

describe('artifacts list rows', () => {
  const long = 'D:\\Müşteri verileri\\2026\\Muhasebe\\Çok uzun bir klasör adı\\alt klasör\\arşiv\\samplefiles-export-with-a-very-long-name'
  it('shows type, source, computer, time, size and availability without opening the detail', async () => {
    mount([artifact(), artifact({ id: 'a2', databaseName: long, backupMetadata: meta('file-system'), agentName: 'FILE-SRV' })])
    const link = await screen.findByRole('link', { name: 'Accounting' })
    const row = link.closest('tr')!
    for (const text of ['MSSQL · SRV-SQL-01', '5 MB', 'Geri yüklemeye hazır']) expect(within(row).getByText(new RegExp(text))).toBeInTheDocument()
    expect(within(row).getByText(/2026/)).toBeInTheDocument()
    expect(screen.getByText('Dosya yedeği · FILE-SRV')).toBeInTheDocument()
  })
  it('truncates a long path visually but keeps the full text as the link name', async () => {
    mount([artifact({ databaseName: long, backupMetadata: meta('file-system') })])
    const link = await screen.findByRole('link', { name: long })
    expect(link).toHaveClass('truncate')
    expect(link).toHaveAttribute('title', long)
  })
  it('omits type and computer when the API did not return them', async () => {
    mount([artifact({ backupMetadata: null, agentName: null })])
    await screen.findByRole('link', { name: 'Accounting' })
    expect(screen.queryByText(/MSSQL|PostgreSQL|Dosya yedeği/)).not.toBeInTheDocument()
  })
  it('renders the same data as cards on a narrow screen', async () => {
    mount([artifact()], false)
    expect(await screen.findByRole('link', { name: 'Accounting' })).toBeInTheDocument()
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    expect(screen.getByText('Geri yüklemeye hazır')).toBeVisible()
    expect(screen.getByText(/MSSQL · SRV-SQL-01/)).toBeVisible()
  })
  it('shows loading, empty and error states', async () => {
    mount('pending')
    expect(screen.getByRole('status')).toHaveTextContent('Yükleniyor')
    cleanup()
    mount([])
    expect(await screen.findByText(/Bu filtrelere uygun yedekleme dosyası yok/)).toBeVisible()
    cleanup()
    mount(new Error('boom'))
    expect(await screen.findByRole('alert')).toBeVisible()
  })
})
