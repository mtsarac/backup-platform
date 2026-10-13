/// <reference lib="es2024.promise" />
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ArtifactRestoreAction } from '@/features/artifacts/artifact-restore-action'
import type { BackupArtifact } from '@/features/artifacts/types'
import { RestoresPage } from '@/pages/restores/restores-page'
import { RestoreDetailPage } from '@/pages/restores/restore-detail-page'
import { RestoreWizardPage } from '@/pages/restores/restore-wizard-page'
import * as RestoreApi from './api'
import * as AgentApi from '@/features/agents/api'
import * as ArtifactApi from '@/features/artifacts/api'
import type { RestorePage, RestoreTargetsResponse, RestoreView } from './types'
import { ApiError } from '@/lib/api/client'

const tenancy = vi.hoisted(() => ({ tenantId: 'tenant-a', canAdmin: true, canOperate: true }))
vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => tenancy }))

vi.mock('./api', () => ({
  getRestores: vi.fn(),
  getRestore: vi.fn(),
  getRestoreTargets: vi.fn(),
  startRestore: vi.fn(),
  startRestorePrecheck: vi.fn(),
  startRestoreDiscovery: vi.fn(),
  cancelRestore: vi.fn(),
}))

vi.mock('@/features/agents/api', () => ({
  getAgents: vi.fn(),
}))

vi.mock('@/features/artifacts/api', () => ({
  getArtifact: vi.fn(),
}))

const sampleArtifact: BackupArtifact = {
  id: 'artifact-1',
  backupType: 'Full',
  databaseName: 'SalesDB',
  state: 'Available',
  sizeBytes: 850000,
  sourceChecksum: 'sha256-test-checksum',
  artifactChecksum: 'sha256-test-checksum',
  checksumAlgorithm: 'sha256',
  createdAt: '2026-10-08T12:00:00Z',
  acceptedAt: '2026-10-08T12:00:00Z',
  retentionExpiresAt: null,
  retentionPinnedAt: null,
  deleteAfter: null,
  deleteReason: null,
  runId: 'run-1',
  jobId: 'job-1',
  attemptId: 'attempt-1',
  cancelDeleteAllowed: true,
  replicas: [
    {
      id: 'replica-1',
      storageProvider: 'central',
      storageConnectionId: 'connection-central',
      storageTarget: 'default',
      key: 'test-key',
      status: 'Available',
      verificationState: 'Verified',
      verificationMethod: 'Checksum',
      usable: true,
      verifiedAt: '2026-10-08T12:01:00Z',
      sizeBytes: 850000,
      checksum: 'sha256-test-checksum',
      checksumAlgorithm: 'sha256',
      deleteAttempts: 0,
      deleteRequestedAt: null,
      deleteAfter: null,
      deleteReason: null,
      deletedAt: null,
      deleteFailureCode: null,
      deleteNextAttemptAt: null,
      deletionExecutor: 'Backend',
      waitingForAgent: false,
      createdAt: '2026-10-08T12:00:00Z',
    },
  ],
}

const sampleTargets: RestoreTargetsResponse = {
  artifactId: 'artifact-1',
  sourceDatabase: 'SalesDB',
  backupType: 'Full',
  sizeBytes: 850000,
  agents: [
    {
      agentId: 'agent-1',
      hostName: 'SQL Server Agent 1',
      status: 'Active',
      connectivity: 'online',
      canRestore: true,
      canPrecheck: true,
      canDiscover: true,
      sourceEligible: true,
      sourceAccess: 'CentralStorage',
      rejectionCode: null,
      rejectionMessage: null,
    },
    {
      agentId: 'agent-2',
      hostName: 'Offline Agent 2',
      status: 'Active',
      connectivity: 'offline',
      canRestore: false,
      canPrecheck: false,
      canDiscover: false,
      sourceEligible: false,
      sourceAccess: null,
      rejectionCode: 'agent_transfer_required',
      rejectionMessage: 'Replica başka bir Agent üzerinde.',
    },
  ],
  targets: [
    {
      jobId: 'job-1',
      jobName: 'Nightly Sales Backup',
      agentId: 'agent-1',
      database: 'SalesDB',
      target: {
        host: 'localhost',
        port: 1433,
        integratedSecurity: true,
        databaseCredentialReference: 'sql_smoke',
        encrypt: true,
        trustServerCertificate: true,
      },
    },
  ],
}

const sampleRestore: RestoreView = {
  id: 'restore-1',
  artifactId: 'artifact-1',
  replicaId: 'replica-1',
  agentId: 'agent-1',
  databaseName: 'RestoredSalesDB',
  status: 'Running',
  mode: 'NewDatabase',
  overwritePolicy: 'Forbidden',
  sourceAccess: 'CentralStorage',
  purpose: 'Execution',
  target: {
    host: 'localhost',
    port: 1433,
    integratedSecurity: true,
    databaseCredentialReference: 'sql_smoke',
    encrypt: true,
    trustServerCertificate: true,
  },
  options: {
    terminateActiveConnections: false,
    verifyChecksum: true,
    verifyBackup: true,
  },
  createdAt: '2026-10-08T12:10:00Z',
  startedAt: '2026-10-08T12:10:05Z',
  completedAt: null,
  cancellationRequestedAt: null,
  failureCode: null,
  reason: null,
  targetState: null,
  targetStateSummary: null,
  progress: { stage: 'Restoring', percent: 65.5, at: '2026-10-08T12:11:00Z' },
  replicaPinned: true,
  result: null,
  attempts: [
    {
      id: 'attempt-1',
      number: 1,
      status: 'Running',
      createdAt: '2026-10-08T12:10:00Z',
      leasedAt: '2026-10-08T12:10:02Z',
      startedAt: '2026-10-08T12:10:05Z',
      completedAt: null,
      failureCode: null,
      reason: null,
      targetState: null,
      progressStage: 'Restoring',
      progressPercent: 65.5,
      progressAt: '2026-10-08T12:11:00Z',
    },
  ],
}

const sampleRestorePage: RestorePage = {
  items: [sampleRestore],
  totalCount: 1,
  page: 1,
  pageSize: 25,
}

const passedPrecheck: RestoreView = {
  ...sampleRestore,
  id: 'precheck-current',
  purpose: 'Precheck',
  status: 'Succeeded',
  result: {
    preCheck: [{ checkId: 'target.safe', status: 'Passed', reasonCode: 'ok', message: 'Current target checked.' }],
    warnings: [], sourceBackup: null, targetServer: null, fileMappings: [],
    startedAt: null, finishedAt: null, restoreStartedAt: null, restoreFinishedAt: null,
    checksumVerified: true, backupVerified: true, databaseState: 'ONLINE', targetStateNote: null,
  },
}

const discoveredDatabases: RestoreView = {
  ...passedPrecheck,
  id: 'discovery-current',
  purpose: 'Discovery',
  result: {
    ...passedPrecheck.result!,
    discovery: {
      instance: { engine: 'MSSQL', serverName: 'localhost', instanceName: null, version: null, edition: null, hostPlatform: null },
      databases: [
        { name: 'SalesDB', isSystem: false, availability: 'ONLINE' },
        { name: 'OtherDB', isSystem: false, availability: 'ONLINE' },
      ],
    },
  },
}

function renderWizard() {
  return renderWithClient(
    <Routes>
      <Route path="/tenants/:tenantId/artifacts/:id/restore" element={<RestoreWizardPage />} />
      <Route path="/tenants/:tenantId/artifacts/:id" element={<p>Backup details opened</p>} />
      <Route path="/tenants/:tenantId/restores/:id" element={<p>Restore execution opened</p>} />
    </Routes>,
    '/tenants/tenant-a/artifacts/artifact-1/restore',
  )
}

async function openModeSettings() {
  fireEvent.click(await screen.findByRole('button', { name: /Devam et: Restore Modu/i }))
}

async function selectReplaceDatabase() {
  fireEvent.click(screen.getByRole('button', { name: /Replace Existing Database/i }))
  const databases = await screen.findByRole('combobox', { name: 'Üzerine Yazılacak Veritabanı' })
  fireEvent.change(databases, { target: { value: 'SalesDB' } })
  fireEvent.change(screen.getByRole('textbox', { name: /Onaylamak için veritabanı adını/i }), { target: { value: 'SalesDB' } })
}

function renderWithClient(ui: React.ReactElement, initialRoute = '/') {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={[initialRoute]}>
        {ui}
      </MemoryRouter>
    </QueryClientProvider>
  )
}

beforeEach(() => {
  tenancy.tenantId = 'tenant-a'
  tenancy.canAdmin = true
  tenancy.canOperate = true

  vi.mocked(RestoreApi.getRestores).mockResolvedValue(sampleRestorePage)
  vi.mocked(RestoreApi.getRestore).mockImplementation((_tenant, id) => Promise.resolve(
    id === discoveredDatabases.id ? discoveredDatabases : id === passedPrecheck.id ? passedPrecheck : sampleRestore,
  ))
  vi.mocked(RestoreApi.getRestoreTargets).mockResolvedValue(sampleTargets)
  vi.mocked(AgentApi.getAgents).mockResolvedValue([
    {
      agentId: 'agent-1',
      hostName: 'SQL Server Agent 1',
      status: 'Active',
      connectivity: 'online',
      operationalStatus: 'Online',
      lastSeenAt: null,
      lastHeartbeatAt: null,
      lastConnectedAt: null,
      platform: 'Linux',
      agentVersion: '1.0',
      protocolVersion: '1.0',
      capabilityRevision: null,
      capabilities: [],
    },
  ])
  vi.mocked(ArtifactApi.getArtifact).mockResolvedValue(sampleArtifact)
  vi.mocked(RestoreApi.startRestorePrecheck).mockResolvedValue(sampleRestore)
  vi.mocked(RestoreApi.startRestore).mockResolvedValue(sampleRestore)
  vi.mocked(RestoreApi.startRestoreDiscovery).mockResolvedValue(discoveredDatabases)
})

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('ArtifactRestoreAction', () => {
  it('renders Restore Backup button when user canOperate and artifact is eligible', () => {
    renderWithClient(<ArtifactRestoreAction tenantId="tenant-a" artifact={sampleArtifact} />)
    const button = screen.getByRole('button', { name: /Restore Backup/i })
    expect(button).toBeEnabled()
  })

  it('blocks Viewer role from restoring', () => {
    tenancy.canOperate = false
    renderWithClient(<ArtifactRestoreAction tenantId="tenant-a" artifact={sampleArtifact} />)
    expect(screen.queryByRole('button', { name: /Restore Backup/i })).not.toBeInTheDocument()
    expect(screen.getByText(/Viewer rolü bu işlem için yetkili değildir/i)).toBeInTheDocument()
  })

  it('shows disabled button and explanation when artifact is deleted or unverified', () => {
    const deletedArtifact: BackupArtifact = { ...sampleArtifact, state: 'Deleted' }
    renderWithClient(<ArtifactRestoreAction tenantId="tenant-a" artifact={deletedArtifact} />)
    const button = screen.getByRole('button', { name: /Restore Backup/i })
    expect(button).toBeDisabled()
    expect(screen.getByText(/silindiği için geri yüklenemez/i)).toBeInTheDocument()
  })
})

describe('RestoresPage', () => {
  it('renders restore history list and filters', async () => {
    renderWithClient(<RestoresPage />)
    expect(screen.getByRole('heading', { name: /Geri yükleme geçmişi/i })).toBeInTheDocument()
    expect(await screen.findByText('RestoredSalesDB')).toBeInTheDocument()
    expect(screen.getByText('Yeni Database')).toBeInTheDocument()
    expect(screen.getByText('Çalışıyor')).toBeInTheDocument()
  })
})

describe('RestoreDetailPage', () => {
  it('renders running restore progress and allows cancellation for operators', async () => {
    renderWithClient(
      <Routes>
        <Route path="/tenants/:tenantId/restores/:id" element={<RestoreDetailPage />} />
      </Routes>,
      '/tenants/tenant-a/restores/restore-1'
    )

    expect(await screen.findByText(/Geri yükleme · RestoredSalesDB/i)).toBeInTheDocument()
    expect(screen.getAllByText(/Geri yükleniyor · %65,5/i).length).toBeGreaterThanOrEqual(1)
    const cancelButton = screen.getByRole('button', { name: /İptal et/i })
    expect(cancelButton).toBeEnabled()

    fireEvent.click(cancelButton)
    expect(await screen.findByText(/Restore iptal edilsin mi\?/i)).toBeInTheDocument()
  })

  it('renders warning when restore failed or cancelled that database may not be healthy', async () => {
    const failedRestore: RestoreView = {
      ...sampleRestore,
      status: 'Cancelled',
      targetState: 'PartiallyRestored',
      failureCode: 'Cancelled',
      targetStateSummary: 'The restore was cancelled.',
    }
    vi.mocked(RestoreApi.getRestore).mockResolvedValue(failedRestore)

    renderWithClient(
      <Routes>
        <Route path="/tenants/:tenantId/restores/:id" element={<RestoreDetailPage />} />
      </Routes>,
      '/tenants/tenant-a/restores/restore-1'
    )

    expect(await screen.findByText(/Cancelled, Failed veya AgentLost durumu Database'in sağlıklı olduğunu göstermez/i)).toBeInTheDocument()
    expect(screen.getByText(/Kısmen geri yüklendi · PartiallyRestored/i)).toBeInTheDocument()
  })
})

describe('RestoreWizardPage', () => {
  it('starts an explicit recheck with a fresh operation key and gates execution until it finishes', async () => {
    const recheck = Promise.withResolvers<RestoreView>()
    vi.mocked(RestoreApi.startRestorePrecheck)
      .mockResolvedValueOnce(passedPrecheck)
      .mockReturnValueOnce(recheck.promise)
    vi.mocked(RestoreApi.getRestore).mockResolvedValue(passedPrecheck)
    renderWizard()
    await openModeSettings()
    fireEvent.click(screen.getByRole('button', { name: /Ön Kontrolü Çalıştır/i }))
    expect(await screen.findByText('Current target checked.')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: /Yeniden Kontrol Et/i }))
    await waitFor(() => expect(RestoreApi.startRestorePrecheck).toHaveBeenCalledTimes(2))
    const [first, second] = vi.mocked(RestoreApi.startRestorePrecheck).mock.calls
    expect(second[3]).not.toBe(first[3])
    expect(second[2]).toEqual(first[2])
    expect(screen.queryByText('Current target checked.')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: /Olarak Restore Başlat/i })).toBeDisabled()
    await act(async () => { recheck.resolve({ ...passedPrecheck, id: 'precheck-fresh' }) })
    expect(await screen.findByRole('button', { name: /Olarak Restore Başlat/i })).toBeEnabled()
    fireEvent.click(screen.getByRole('button', { name: /Olarak Restore Başlat/i }))
    await waitFor(() => expect(RestoreApi.startRestore).toHaveBeenCalled())
    expect(vi.mocked(RestoreApi.startRestore).mock.calls[0][2].precheckId).toBe('precheck-fresh')
  })

  it.each([new TypeError('network lost'), new ApiError(503)])('retries an ambiguous precheck with the same key and payload: %s', async (failure) => {
    vi.mocked(RestoreApi.startRestorePrecheck).mockRejectedValueOnce(failure).mockResolvedValueOnce(passedPrecheck)
    vi.mocked(RestoreApi.getRestore).mockResolvedValue(passedPrecheck)
    renderWizard()
    await openModeSettings()
    fireEvent.click(screen.getByRole('button', { name: /Ön Kontrolü Çalıştır/i }))
    expect(await screen.findByRole('alert')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: /Tekrar Dene/i }))
    expect(await screen.findByText('Current target checked.')).toBeInTheDocument()
    const [first, second] = vi.mocked(RestoreApi.startRestorePrecheck).mock.calls
    expect(second[3]).toBe(first[3])
    expect(second[2]).toBe(first[2])
  })

  it('starts a fresh precheck after a definitive rejection', async () => {
    vi.mocked(RestoreApi.startRestorePrecheck).mockRejectedValueOnce(new ApiError(409)).mockResolvedValueOnce(passedPrecheck)
    renderWizard()
    await openModeSettings()
    fireEvent.click(screen.getByRole('button', { name: /Ön Kontrolü Çalıştır/i }))
    expect(await screen.findByRole('alert')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: /Ön Kontrolü Çalıştır/i }))
    expect(await screen.findByText('Current target checked.')).toBeInTheDocument()
    const [first, second] = vi.mocked(RestoreApi.startRestorePrecheck).mock.calls
    expect(second[3]).not.toBe(first[3])
  })

  it('abandons an ambiguous precheck attempt after database A→B→A rather than replaying its key', async () => {
    vi.mocked(RestoreApi.startRestorePrecheck).mockRejectedValueOnce(new TypeError('response lost')).mockResolvedValueOnce(passedPrecheck)
    renderWizard()
    await openModeSettings()
    fireEvent.click(screen.getByRole('button', { name: /Ön Kontrolü Çalıştır/i }))
    expect(await screen.findByRole('alert')).toBeInTheDocument()
    const database = screen.getByRole('textbox', { name: 'Yeni Veritabanı Adı' })
    fireEvent.change(database, { target: { value: 'OtherDB' } })
    fireEvent.change(database, { target: { value: 'SalesDB_restored' } })
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: /Ön Kontrolü Çalıştır/i }))
    expect(await screen.findByText('Current target checked.')).toBeInTheDocument()
    const [first, fresh] = vi.mocked(RestoreApi.startRestorePrecheck).mock.calls
    expect(fresh[3]).not.toBe(first[3])
    expect(fresh[2]).toEqual(first[2])
  })

  it('retries ambiguous discovery unchanged, but a fresh refresh gets a new key and clears the selection', async () => {
    vi.mocked(RestoreApi.startRestoreDiscovery)
      .mockRejectedValueOnce(new TypeError('connection lost'))
      .mockResolvedValue(discoveredDatabases)
    renderWizard()
    await openModeSettings()
    fireEvent.click(screen.getByRole('button', { name: /Replace Existing Database/i }))
    expect(await screen.findByRole('alert')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Tekrar Dene' }))
    const databases = await screen.findByRole('combobox', { name: 'Üzerine Yazılacak Veritabanı' })
    fireEvent.change(databases, { target: { value: 'SalesDB' } })
    fireEvent.change(screen.getByRole('textbox', { name: /Onaylamak için/i }), { target: { value: 'SalesDB' } })
    fireEvent.click(screen.getByRole('button', { name: 'Sunucudan Yenile' }))
    expect(await screen.findByRole('combobox', { name: 'Üzerine Yazılacak Veritabanı' })).toHaveValue('')
    expect(screen.getByRole('textbox', { name: /Onaylamak için/i })).toHaveValue('')
    const [first, retry, fresh] = vi.mocked(RestoreApi.startRestoreDiscovery).mock.calls
    expect(retry[3]).toBe(first[3])
    expect(retry[2]).toBe(first[2])
    expect(fresh[3]).not.toBe(first[3])
    expect(screen.getByRole('button', { name: /Ön Kontrolü Çalıştır/i })).toBeDisabled()
  })

  it('retries ambiguous execution with the same key and immutable payload, then uses a fresh key after input changes', async () => {
    vi.mocked(RestoreApi.startRestorePrecheck).mockResolvedValue(passedPrecheck)
    vi.mocked(RestoreApi.startRestore)
      .mockRejectedValueOnce(new TypeError('response lost'))
      .mockRejectedValueOnce(new ApiError(503))
      .mockResolvedValueOnce(sampleRestore)
    renderWizard()
    await openModeSettings()
    fireEvent.click(screen.getByRole('button', { name: /Ön Kontrolü Çalıştır/i }))
    fireEvent.click(await screen.findByRole('button', { name: /Olarak Restore Başlat/i }))
    expect(await screen.findByRole('alert')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Tekrar Dene' }))
    await waitFor(() => expect(RestoreApi.startRestore).toHaveBeenCalledTimes(2))
    expect(await screen.findByRole('button', { name: 'Tekrar Dene' })).toBeEnabled()
    const [first, retry] = vi.mocked(RestoreApi.startRestore).mock.calls
    expect(retry[3]).toBe(first[3])
    expect(retry[2]).toBe(first[2])
    fireEvent.click(screen.getByRole('button', { name: /Mod Ayarlarına Dön/i }))
    fireEvent.change(screen.getByRole('textbox', { name: 'Yeni Veritabanı Adı' }), { target: { value: 'ChangedDB' } })
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: /Ön Kontrolü Çalıştır/i }))
    fireEvent.click(await screen.findByRole('button', { name: /Olarak Restore Başlat/i }))
    expect(await screen.findByText('Restore execution opened')).toBeInTheDocument()
    const fresh = vi.mocked(RestoreApi.startRestore).mock.calls[2]
    expect(fresh[3]).not.toBe(first[3])
    expect(fresh[2].databaseName).toBe('ChangedDB')
    expect(first[2].databaseName).toBe('SalesDB_restored')
  })

  it.each(['database', 'mode', 'checksum', 'verify backup', 'terminate connections'] as const)(
    'invalidates an in-flight destructive precheck on %s A→B→A and ignores its late response',
    async (field) => {
      const obsolete = Promise.withResolvers<RestoreView>()
      vi.mocked(RestoreApi.startRestorePrecheck).mockReturnValueOnce(obsolete.promise).mockResolvedValueOnce(passedPrecheck)
      renderWizard()
      await openModeSettings()
      await selectReplaceDatabase()
      const run = screen.getByRole('button', { name: /Ön Kontrolü Çalıştır/i })
      fireEvent.click(run)
      fireEvent.click(run)
      await waitFor(() => expect(RestoreApi.startRestorePrecheck).toHaveBeenCalledTimes(1))
      if (field === 'database') {
        const databases = screen.getByRole('combobox', { name: 'Üzerine Yazılacak Veritabanı' })
        fireEvent.change(databases, { target: { value: 'OtherDB' } })
        fireEvent.change(databases, { target: { value: 'SalesDB' } })
      } else if (field === 'mode') {
        fireEvent.click(screen.getByRole('button', { name: /New Database/i }))
        fireEvent.click(screen.getByRole('button', { name: /Replace Existing Database/i }))
        fireEvent.change(await screen.findByRole('combobox', { name: 'Üzerine Yazılacak Veritabanı' }), { target: { value: 'SalesDB' } })
      } else {
        const name = field === 'checksum' ? 'SHA-256 Checksum Doğrula'
          : field === 'verify backup' ? 'RESTORE VERIFYONLY Çalıştır'
          : 'Mevcut aktif bağlantıları sonlandır (WITH ROLLBACK IMMEDIATE)'
        fireEvent.click(screen.getByRole('checkbox', { name }))
        fireEvent.click(screen.getByRole('checkbox', { name }))
      }
      expect(screen.getByRole('textbox', { name: /Onaylamak için/i })).toHaveValue('')
      await act(async () => { obsolete.resolve(passedPrecheck) })
      expect(screen.queryByText('Current target checked.')).not.toBeInTheDocument()
      expect(screen.queryByRole('button', { name: /Olarak Restore Başlat/i })).not.toBeInTheDocument()
      expect(RestoreApi.startRestore).not.toHaveBeenCalled()
      fireEvent.change(screen.getByRole('textbox', { name: /Onaylamak için/i }), { target: { value: 'SalesDB' } })
      await waitFor(() => expect(screen.getByRole('button', { name: /Ön Kontrolü Çalıştır/i })).toBeEnabled())
      fireEvent.click(screen.getByRole('button', { name: /Ön Kontrolü Çalıştır/i }))
      expect(await screen.findByText('Current target checked.')).toBeInTheDocument()
      const [first, second] = vi.mocked(RestoreApi.startRestorePrecheck).mock.calls
      expect(second[3]).not.toBe(first[3])
      expect(second[2]).toEqual(first[2])
    },
  )

  it.each(['agent', 'configured target', 'target type', 'host', 'port', 'credential', 'encrypt', 'trust certificate'] as const)(
    'clears discovery, selection and precheck on target %s A→B→A and ignores obsolete polling',
    async (field) => {
      const obsoletePoll = Promise.withResolvers<RestoreView>()
      vi.mocked(RestoreApi.getRestoreTargets).mockResolvedValue({
        ...sampleTargets,
        agents: [sampleTargets.agents[0], { ...sampleTargets.agents[0], agentId: 'agent-other' }],
        targets: [sampleTargets.targets[0], { ...sampleTargets.targets[0], jobId: 'job-other', target: { ...sampleTargets.targets[0].target, host: 'other-server' } }],
      })
      vi.mocked(RestoreApi.startRestorePrecheck).mockResolvedValue({ ...passedPrecheck, status: 'Running', result: null })
      vi.mocked(RestoreApi.getRestore).mockImplementation((_tenant, id) => id === passedPrecheck.id ? obsoletePoll.promise : Promise.resolve(discoveredDatabases))
      renderWizard()
      await screen.findByRole('combobox', { name: 'Agent' })
      const custom = !['agent', 'configured target', 'target type'].includes(field)
      if (custom) fireEvent.click(screen.getByRole('radio', { name: 'Özel Bağlantı' }))
      await openModeSettings()
      await selectReplaceDatabase()
      fireEvent.click(screen.getByRole('button', { name: /Ön Kontrolü Çalıştır/i }))
      expect(await screen.findByRole('button', { name: /Olarak Restore Başlat/i })).toBeDisabled()
      await waitFor(() => expect(RestoreApi.getRestore).toHaveBeenCalledWith('tenant-a', passedPrecheck.id, expect.anything()))
      fireEvent.click(screen.getByRole('button', { name: /Mod Ayarlarına Dön/i }))
      fireEvent.click(screen.getByRole('button', { name: /← Geri/i }))
      if (field === 'agent' || field === 'configured target') {
        const select = screen.getByRole('combobox', { name: field === 'agent' ? 'Agent' : 'Bağlantı' })
        const original = field === 'agent' ? 'agent-1' : '0'
        fireEvent.change(select, { target: { value: field === 'agent' ? 'agent-other' : '1' } })
        fireEvent.change(select, { target: { value: original } })
      } else if (field === 'target type') {
        fireEvent.click(screen.getByRole('radio', { name: 'Özel Bağlantı' }))
        fireEvent.click(screen.getByRole('radio', { name: /Kayıtlı İş Bağlantısı/i }))
      } else if (field === 'encrypt' || field === 'trust certificate') {
        const name = field === 'encrypt' ? 'Bağlantıyı Şifrele' : 'Sunucu Sertifikasına Güven'
        fireEvent.click(screen.getByRole('checkbox', { name }))
        fireEvent.click(screen.getByRole('checkbox', { name }))
      } else {
        const name = field === 'host' ? 'Host / Instance' : field === 'port' ? 'Port (İsteğe bağlı)' : 'Credential Referansı'
        const input = screen.getByLabelText(name)
        const original = field === 'host' ? 'localhost' : field === 'port' ? '1433' : 'sql_smoke'
        fireEvent.change(input, { target: { value: field === 'port' ? '1444' : 'changed-value' } })
        fireEvent.change(input, { target: { value: original } })
      }
      await openModeSettings()
      expect(screen.queryByRole('combobox', { name: 'Üzerine Yazılacak Veritabanı' })).not.toBeInTheDocument()
      expect(screen.getByRole('textbox', { name: 'Üzerine Yazılacak Veritabanı' })).toHaveValue('')
      expect(screen.getByRole('textbox', { name: /Onaylamak için/i })).toHaveValue('')
      await act(async () => { obsoletePoll.resolve(passedPrecheck) })
      expect(screen.queryByText('Current target checked.')).not.toBeInTheDocument()
      expect(screen.queryByRole('button', { name: /Olarak Restore Başlat/i })).not.toBeInTheDocument()
      expect(RestoreApi.startRestore).not.toHaveBeenCalled()
    },
  )

  it('ignores a late destructive precheck rejection after database changes', async () => {
    const obsolete = Promise.withResolvers<RestoreView>()
    vi.mocked(RestoreApi.startRestorePrecheck).mockReturnValueOnce(obsolete.promise)
    renderWizard()
    await openModeSettings()
    await selectReplaceDatabase()
    fireEvent.click(screen.getByRole('button', { name: /Ön Kontrolü Çalıştır/i }))
    await waitFor(() => expect(RestoreApi.startRestorePrecheck).toHaveBeenCalledTimes(1))
    fireEvent.change(screen.getByRole('combobox', { name: 'Üzerine Yazılacak Veritabanı' }), { target: { value: 'OtherDB' } })
    await act(async () => { obsolete.reject(new ApiError(503)) })
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Tekrar Dene' })).not.toBeInTheDocument()
    await waitFor(() => expect(screen.getByRole('button', { name: /Ön Kontrolü Çalıştır/i })).toBeDisabled())
  })

  it('ignores late discovery polling after target changes', async () => {
    const obsolete = Promise.withResolvers<RestoreView>()
    vi.mocked(RestoreApi.startRestoreDiscovery).mockResolvedValueOnce({ ...discoveredDatabases, status: 'Running', result: null })
    vi.mocked(RestoreApi.getRestore).mockReturnValueOnce(obsolete.promise)
    renderWizard()
    await openModeSettings()
    const replace = screen.getByRole('button', { name: /Replace Existing Database/i })
    fireEvent.click(replace)
    fireEvent.click(replace)
    await waitFor(() => expect(RestoreApi.startRestoreDiscovery).toHaveBeenCalledTimes(1))
    await waitFor(() => expect(RestoreApi.getRestore).toHaveBeenCalled())
    fireEvent.click(screen.getByRole('button', { name: /← Geri/i }))
    fireEvent.click(screen.getByRole('radio', { name: 'Özel Bağlantı' }))
    fireEvent.change(screen.getByRole('textbox', { name: 'Host / Instance' }), { target: { value: 'other-server' } })
    await openModeSettings()
    await act(async () => { obsolete.resolve(discoveredDatabases) })
    expect(screen.queryByRole('combobox', { name: 'Üzerine Yazılacak Veritabanı' })).not.toBeInTheDocument()
    expect(screen.getByRole('textbox', { name: 'Üzerine Yazılacak Veritabanı' })).toHaveValue('')
    expect(screen.getByRole('button', { name: /Ön Kontrolü Çalıştır/i })).toBeDisabled()
  })

  it.each(['success', 'error'] as const)('ignores a late discovery %s after target A→B→A', async (completion) => {
    const obsolete = Promise.withResolvers<RestoreView>()
    vi.mocked(RestoreApi.startRestoreDiscovery).mockReturnValueOnce(obsolete.promise)
    renderWizard()
    await openModeSettings()
    fireEvent.click(screen.getByRole('button', { name: /Replace Existing Database/i }))
    await waitFor(() => expect(RestoreApi.startRestoreDiscovery).toHaveBeenCalledTimes(1))
    fireEvent.click(screen.getByRole('button', { name: /← Geri/i }))
    const agent = screen.getByRole('combobox', { name: 'Agent' })
    fireEvent.change(agent, { target: { value: 'agent-2' } })
    fireEvent.change(agent, { target: { value: 'agent-1' } })
    await openModeSettings()
    await act(async () => {
      if (completion === 'success') obsolete.resolve(discoveredDatabases)
      else obsolete.reject(new ApiError(503))
    })
    expect(screen.queryByRole('combobox', { name: 'Üzerine Yazılacak Veritabanı' })).not.toBeInTheDocument()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    expect(screen.getByRole('textbox', { name: 'Üzerine Yazılacak Veritabanı' })).toHaveValue('')
    expect(screen.getByRole('button', { name: /Ön Kontrolü Çalıştır/i })).toBeDisabled()
  })

  it('ignores execution completion after its inputs change', async () => {
    const obsolete = Promise.withResolvers<RestoreView>()
    vi.mocked(RestoreApi.startRestorePrecheck).mockResolvedValue(passedPrecheck)
    vi.mocked(RestoreApi.startRestore).mockReturnValueOnce(obsolete.promise)
    renderWizard()
    await openModeSettings()
    fireEvent.click(screen.getByRole('button', { name: /Ön Kontrolü Çalıştır/i }))
    const execute = await screen.findByRole('button', { name: /Olarak Restore Başlat/i })
    fireEvent.click(execute)
    fireEvent.click(execute)
    await waitFor(() => expect(RestoreApi.startRestore).toHaveBeenCalledTimes(1))
    fireEvent.click(screen.getByRole('button', { name: /Mod Ayarlarına Dön/i }))
    fireEvent.change(screen.getByRole('textbox', { name: 'Yeni Veritabanı Adı' }), { target: { value: 'ChangedDB' } })
    await act(async () => { obsolete.resolve(sampleRestore) })
    expect(screen.queryByText('Restore execution opened')).not.toBeInTheDocument()
    expect(screen.getByRole('textbox', { name: 'Yeni Veritabanı Adı' })).toHaveValue('ChangedDB')
  })

  it('does not navigate back to a late execution after leaving the wizard', async () => {
    const obsolete = Promise.withResolvers<RestoreView>()
    vi.mocked(RestoreApi.startRestorePrecheck).mockResolvedValue(passedPrecheck)
    vi.mocked(RestoreApi.startRestore).mockReturnValueOnce(obsolete.promise)
    renderWizard()
    await openModeSettings()
    fireEvent.click(screen.getByRole('button', { name: /Ön Kontrolü Çalıştır/i }))
    fireEvent.click(await screen.findByRole('button', { name: /Olarak Restore Başlat/i }))
    await waitFor(() => expect(RestoreApi.startRestore).toHaveBeenCalledTimes(1))
    fireEvent.click(screen.getByRole('link', { name: /Backup detaylarına dön/i }))
    expect(await screen.findByText('Backup details opened')).toBeInTheDocument()
    await act(async () => { obsolete.resolve(sampleRestore) })
    expect(screen.getByText('Backup details opened')).toBeInTheDocument()
    expect(screen.queryByText('Restore execution opened')).not.toBeInTheDocument()
  })

  it.each(['CentralStorage', 'BackendRelayed', 'AgentLocal'] as const)('uses the eligible %s agent through precheck and execution', async (sourceAccess) => {
    const precheckResult: RestoreView = {
      ...sampleRestore,
      id: 'precheck-op-1',
      purpose: 'Precheck',
      status: 'Succeeded',
      targetState: 'NotModified',
      result: {
        checksumVerified: true,
        backupVerified: true,
        databaseState: 'ONLINE',
        targetStateNote: null,
        preCheck: [
          { checkId: 'server.reachable', status: 'Passed', reasonCode: 'ok', message: 'SQL Server reachable.' },
          { checkId: 'database.exists', status: 'Passed', reasonCode: 'ok', message: 'Target database does not exist.' },
        ],
        warnings: [],
        sourceBackup: {
          databaseName: 'SalesDB',
          backupType: 'Full',
          sourceServerName: 'SRV1',
          sourceServerVersion: '16.0',
          backupStartedAt: null,
          backupFinishedAt: null,
          position: 1,
          backupSizeBytes: 850000,
          compatibilityLevel: 160,
          collation: null,
          isCopyOnly: true,
          hasChecksums: true,
          isCompressed: true,
        },
        targetServer: {
          serverName: 'SRV2',
          instanceName: 'MSSQLSERVER',
          version: '16.0.1000',
          edition: 'Developer',
          hostPlatform: 'Linux',
        },
        fileMappings: [],
        startedAt: null,
        finishedAt: null,
        restoreStartedAt: null,
        restoreFinishedAt: null,
      },
    }
    vi.mocked(RestoreApi.getRestoreTargets).mockResolvedValue({
      ...sampleTargets,
      agents: [sampleTargets.agents[1], { ...sampleTargets.agents[0], sourceAccess }],
    })
    vi.mocked(RestoreApi.startRestorePrecheck).mockResolvedValue(precheckResult)
    vi.mocked(RestoreApi.getRestore).mockImplementation((_tenantId, id) => {
      if (id === 'precheck-op-1') return Promise.resolve(precheckResult)
      return Promise.resolve(sampleRestore)
    })
    vi.mocked(RestoreApi.startRestore).mockResolvedValue(sampleRestore)

    renderWithClient(
      <Routes>
        <Route path="/tenants/:tenantId/artifacts/:id/restore" element={<RestoreWizardPage />} />
      </Routes>,
      '/tenants/tenant-a/artifacts/artifact-1/restore'
    )

    // Step 1: Target & Instance
    expect(await screen.findByRole('heading', { name: /1\. Hedef Agent Seçimi/i })).toBeInTheDocument()
    const continueBtn = screen.getByRole('button', { name: /Devam et: Restore Modu/i })
    expect(continueBtn).toBeEnabled()
    expect(screen.getByRole('combobox', { name: 'Agent' })).toHaveValue('agent-1')
    fireEvent.click(continueBtn)

    // Step 2: Restore Mode
    expect(await screen.findByRole('heading', { name: /Geri Yükleme Modu ve Hedef Veritabanı/i })).toBeInTheDocument()
    const precheckBtn = screen.getByRole('button', { name: /Ön Kontrolü Çalıştır/i })
    expect(precheckBtn).toBeEnabled()
    fireEvent.click(precheckBtn)

    // Step 3: Precheck results
    await waitFor(() => expect(RestoreApi.startRestorePrecheck).toHaveBeenCalled())
    expect(RestoreApi.startRestorePrecheck).toHaveBeenCalledWith('tenant-a', 'artifact-1', expect.objectContaining({
      agentId: 'agent-1', target: sampleTargets.targets[0].target, databaseName: 'SalesDB_restored', mode: 'NewDatabase',
    }), expect.any(String))
    expect(await screen.findByText(/Tüm ön kontroller başarıyla tamamlandı/i)).toBeInTheDocument()

    // Step 4: Execute
    const executeBtn = screen.getByRole('button', { name: /Yeni Database Olarak Restore Başlat/i })
    expect(executeBtn).toBeEnabled()
    fireEvent.click(executeBtn)

    await waitFor(() => expect(RestoreApi.startRestore).toHaveBeenCalled())
    expect(RestoreApi.startRestore).toHaveBeenCalledWith('tenant-a', 'artifact-1', expect.objectContaining({
      agentId: 'agent-1', target: sampleTargets.targets[0].target, precheckId: 'precheck-op-1',
    }), expect.any(String))
  })

  it('blocks an ineligible agent without sending a precheck and allows an eligible selection', async () => {
    renderWithClient(<Routes><Route path="/tenants/:tenantId/artifacts/:id/restore" element={<RestoreWizardPage />} /></Routes>, '/tenants/tenant-a/artifacts/artifact-1/restore')
    const selector = await screen.findByRole('combobox', { name: 'Agent' })
    fireEvent.change(selector, { target: { value: 'agent-2' } })
    const next = screen.getByRole('button', { name: /Devam et: Restore Modu/i })
    expect(next).toBeDisabled()
    fireEvent.click(next)
    expect(RestoreApi.startRestorePrecheck).not.toHaveBeenCalled()
    expect(RestoreApi.startRestore).not.toHaveBeenCalled()
    fireEvent.change(selector, { target: { value: 'agent-1' } })
    expect(next).toBeEnabled()
  })

  it('prevents execution when a BackendRelayed precheck fails', async () => {
    vi.mocked(RestoreApi.getRestoreTargets).mockResolvedValue({ ...sampleTargets, agents: [{ ...sampleTargets.agents[0], sourceAccess: 'BackendRelayed' }] })
    const failed: RestoreView = { ...sampleRestore, id: 'failed-precheck', purpose: 'Precheck', status: 'Failed', failureCode: 'StorageUnavailable', reason: null }
    vi.mocked(RestoreApi.startRestorePrecheck).mockResolvedValue(failed)
    vi.mocked(RestoreApi.getRestore).mockResolvedValue(failed)
    renderWithClient(<Routes><Route path="/tenants/:tenantId/artifacts/:id/restore" element={<RestoreWizardPage />} /></Routes>, '/tenants/tenant-a/artifacts/artifact-1/restore')
    fireEvent.click(await screen.findByRole('button', { name: /Devam et: Restore Modu/i }))
    fireEvent.click(screen.getByRole('button', { name: /Ön Kontrolü Çalıştır/i }))
    expect(await screen.findByRole('alert')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /Yeni Database Olarak Restore Başlat/i })).toBeDisabled()
    expect(RestoreApi.startRestore).not.toHaveBeenCalled()
  })

  it('requires exact confirmation text for ReplaceExistingDatabase mode', async () => {
    renderWithClient(
      <Routes>
        <Route path="/tenants/:tenantId/artifacts/:id/restore" element={<RestoreWizardPage />} />
      </Routes>,
      '/tenants/tenant-a/artifacts/artifact-1/restore'
    )

    fireEvent.click(await screen.findByRole('button', { name: /Devam et: Restore Modu/i }))

    // Switch to ReplaceExistingDatabase
    const replaceModeBtn = screen.getByText(/Replace Existing Database/i)
    fireEvent.click(replaceModeBtn)

    expect(screen.getByText(/DİKKAT: Veri Kaybı Riski!/i)).toBeInTheDocument()

    // Precheck button should be disabled until confirmation text matches
    const precheckBtn = screen.getByRole('button', { name: /Ön Kontrolü Çalıştır/i })
    expect(precheckBtn).toBeDisabled()

    fireEvent.change(await screen.findByRole('combobox', { name: 'Üzerine Yazılacak Veritabanı' }), { target: { value: 'SalesDB' } })
    // Type wrong confirmation
    const confirmInput = screen.getByRole('textbox', { name: /Onaylamak için/i })
    fireEvent.change(confirmInput, { target: { value: 'wrong-db-name' } })
    expect(precheckBtn).toBeDisabled()

    // Type exact match
    fireEvent.change(confirmInput, { target: { value: 'SalesDB' } })
    expect(precheckBtn).toBeEnabled()
  })
})
