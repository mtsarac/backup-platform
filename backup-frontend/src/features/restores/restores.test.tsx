import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
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
  vi.mocked(RestoreApi.getRestore).mockResolvedValue(sampleRestore)
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
  it('navigates through agent selection, NewDatabase mode, precheck, and starts restore', async () => {
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
    fireEvent.click(continueBtn)

    // Step 2: Restore Mode
    expect(await screen.findByRole('heading', { name: /Geri Yükleme Modu ve Hedef Veritabanı/i })).toBeInTheDocument()
    const precheckBtn = screen.getByRole('button', { name: /Ön Kontrolü Çalıştır/i })
    expect(precheckBtn).toBeEnabled()
    fireEvent.click(precheckBtn)

    // Step 3: Precheck results
    await waitFor(() => expect(RestoreApi.startRestorePrecheck).toHaveBeenCalled())
    expect(await screen.findByText(/Tüm ön kontroller başarıyla tamamlandı/i)).toBeInTheDocument()

    // Step 4: Execute
    const executeBtn = screen.getByRole('button', { name: /Yeni Database Olarak Restore Başlat/i })
    expect(executeBtn).toBeEnabled()
    fireEvent.click(executeBtn)

    await waitFor(() => expect(RestoreApi.startRestore).toHaveBeenCalled())
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

    // Type wrong confirmation
    const confirmInput = screen.getByPlaceholderText(/SalesDB_restored/i)
    fireEvent.change(confirmInput, { target: { value: 'wrong-db-name' } })
    expect(precheckBtn).toBeDisabled()

    // Type exact match
    fireEvent.change(confirmInput, { target: { value: 'SalesDB_restored' } })
    expect(precheckBtn).toBeEnabled()
  })
})
