import { cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react'
import { render } from '@/test/render'
import { ApiError } from '@/lib/api/client'
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
import type { RestorePage, RestoreResult, RestoreTargetsResponse, RestoreView } from './types'

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
  storeDatabaseLogin: vi.fn(),
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
    const button = screen.getByRole('button', { name: /Yedeklemeyi Geri Yükle/i })
    expect(button).toBeEnabled()
  })

  it('blocks Viewer role from restoring', () => {
    tenancy.canOperate = false
    renderWithClient(<ArtifactRestoreAction tenantId="tenant-a" artifact={sampleArtifact} />)
    expect(screen.queryByRole('button', { name: /Yedeklemeyi Geri Yükle/i })).not.toBeInTheDocument()
    expect(screen.getByText(/Görüntüleyici rolü bu işlem için yetkili değildir/i)).toBeInTheDocument()
  })

  it('shows disabled button and explanation when artifact is deleted or unverified', () => {
    const deletedArtifact: BackupArtifact = { ...sampleArtifact, state: 'Deleted' }
    renderWithClient(<ArtifactRestoreAction tenantId="tenant-a" artifact={deletedArtifact} />)
    const button = screen.getByRole('button', { name: /Yedeklemeyi Geri Yükle/i })
    expect(button).toBeDisabled()
    expect(screen.getByText(/silindiği için geri yüklenemez/i)).toBeInTheDocument()
  })
})

describe('RestoresPage', () => {
  it('renders restore history list and filters', async () => {
    renderWithClient(<RestoresPage />)
    expect(screen.getByRole('heading', { name: /Geri yükleme geçmişi/i })).toBeInTheDocument()
    expect(await screen.findByText('RestoredSalesDB')).toBeInTheDocument()
    expect(screen.getByText("Yeni veritabanı")).toBeInTheDocument()
    expect(screen.getAllByText('Çalışıyor').length).toBeGreaterThan(0)
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
    expect(await screen.findByText(/Geri Yükleme iptal edilsin mi\?/i)).toBeInTheDocument()
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

    expect(await screen.findByText(/veritabanının sağlıklı olduğunu göstermez/i)).toBeInTheDocument()
    expect(screen.getByText(/Kısmen geri yüklendi/i)).toBeInTheDocument()
  })

  it('keeps source safety options and attempt progress accessible in their tabs', async () => {
    renderWithClient(<Routes><Route path="/tenants/:tenantId/restores/:id" element={<RestoreDetailPage />} /></Routes>, '/tenants/tenant-a/restores/restore-1')
    expect(await screen.findByRole('heading', { name: /Geri yükleme · RestoredSalesDB/i })).toBeInTheDocument()
    fireEvent.click(screen.getByRole('tab', { name: 'Kaynak ve seçenekler' }))
    expect(screen.getByText("Geri yükleme için silmeye karşı korunuyor")).toBeInTheDocument()
    expect(screen.getByText("Etkin bağlantıları sonlandır")).toBeInTheDocument()
    fireEvent.click(screen.getByRole('tab', { name: 'Denemeler' }))
    expect(screen.getByRole('article', { name: '1. deneme' })).toBeInTheDocument()
    expect(screen.getByRole('progressbar', { name: '1. deneme ilerlemesi' })).toHaveAttribute('aria-valuenow', '65.5')
    fireEvent.click(screen.getByRole('tab', { name: 'Doğrulama sonucu' }))
    expect(screen.getByText(/Doğrulama sonucu henüz bildirilmedi/)).toBeInTheDocument()
  })
})

const wizardRoutes = <Routes><Route path="/tenants/:tenantId/artifacts/:id/restore" element={<RestoreWizardPage />} /></Routes>
const wizardUrl = '/tenants/tenant-a/artifacts/artifact-1/restore'
const passedPrecheck = (id: string): RestoreView => ({ ...sampleRestore, id, purpose: 'Precheck', status: 'Succeeded' })
/** The progress indicator's current step: the one numbering scheme the wizard shows. */
const stepperCurrent = () => screen.getByRole('listitem', { current: 'step' }).textContent ?? ''
/** Walks an eligible wizard through where, how and check up to the confirmation step. */
async function reachConfirmStep(how: 'new' | 'replace' = 'new') {
  fireEvent.click(await screen.findByRole('button', { name: /Devam et/i }))
  if (how === 'replace') {
    fireEvent.click(screen.getByRole('button', { name: /Mevcut bir veritabanının yerine/i }))
    await waitFor(() => expect(RestoreApi.startRestoreDiscovery).toHaveBeenCalled())
  }
  fireEvent.click(screen.getByRole('button', { name: /Ön Kontrolü Çalıştır/i }))
  fireEvent.click(await screen.findByRole('button', { name: /Devam et/i }))
  await screen.findByRole('button', { name: /Geri yüklemeyi başlat/i })
}

describe('RestoreWizardPage', () => {
  it('routes a custom PostgreSQL target with password authentication and rejects identifiers over 63 UTF-8 bytes', async () => {
    vi.mocked(RestoreApi.getRestoreTargets).mockResolvedValue({ ...sampleTargets, engine: 'postgres', targets: [] })
    renderWithClient(<Routes><Route path="/tenants/:tenantId/artifacts/:id/restore" element={<RestoreWizardPage />} /></Routes>, '/tenants/tenant-a/artifacts/artifact-1/restore')
    const host = await screen.findByLabelText("PostgreSQL Sunucusu")
    expect(screen.getByLabelText(/Bağlantı Noktası/)).toHaveValue(5432)
    expect(screen.getByRole('button', { name: /Devam et/i })).toBeDisabled()
    fireEvent.change(host, { target: { value: 'pg-target' } })
    vi.mocked(RestoreApi.storeDatabaseLogin).mockResolvedValue({ reference: 'pg-restore' })
    fireEvent.change(screen.getByLabelText("Veritabanı kullanıcı adı"), { target: { value: 'pg_user' } })
    fireEvent.change(screen.getByLabelText("Veritabanı parolası"), { target: { value: 'pg-secret' } })
    fireEvent.click(screen.getByRole('button', { name: 'Girişi kaydet' }))
    await waitFor(() => expect(RestoreApi.storeDatabaseLogin).toHaveBeenCalledWith('tenant-a', expect.any(String), { userName: 'pg_user', password: 'pg-secret' }))
    fireEvent.click(screen.getByRole('button', { name: /Devam et/i }))
    expect(screen.queryByRole('checkbox', { name: /RESTORE VERIFYONLY/ })).not.toBeInTheDocument()
    fireEvent.change(screen.getByLabelText('Yeni Veritabanı Adı'), { target: { value: '数'.repeat(22) } })
    expect(screen.getByRole('button', { name: /Ön Kontrolü Çalıştır/i })).toBeDisabled()
    fireEvent.change(screen.getByLabelText('Yeni Veritabanı Adı'), { target: { value: '数'.repeat(21) } })
    fireEvent.click(screen.getByRole('button', { name: /Ön Kontrolü Çalıştır/i }))
    await waitFor(() => expect(RestoreApi.startRestorePrecheck).toHaveBeenCalledTimes(1))
    expect(vi.mocked(RestoreApi.startRestorePrecheck).mock.calls[0].slice(0, 3)).toEqual([
      'tenant-a', 'artifact-1', expect.objectContaining({
        databaseName: '数'.repeat(21), target: expect.objectContaining({
          host: 'pg-target', port: 5432, integratedSecurity: false, databaseCredentialReference: 'pg-restore',
        }),
      }),
    ])
  })
  it('uses a new precheck idempotency key after correcting wizard input', async () => {
    const precheck = { ...sampleRestore, id: 'precheck-correction', purpose: 'Precheck' as const, status: 'Succeeded' as const }
    vi.mocked(RestoreApi.startRestorePrecheck).mockResolvedValue(precheck)
    vi.mocked(RestoreApi.getRestore).mockResolvedValue(precheck)
    renderWithClient(<Routes><Route path="/tenants/:tenantId/artifacts/:id/restore" element={<RestoreWizardPage />} /></Routes>, '/tenants/tenant-a/artifacts/artifact-1/restore')
    fireEvent.click(await screen.findByRole('button', { name: /Devam et/i }))
    fireEvent.click(screen.getByRole('button', { name: /Ön Kontrolü Çalıştır/i }))
    await screen.findByRole('button', { name: /Devam et/i })
    fireEvent.click(screen.getByRole('button', { name: /^← Geri/i }))
    fireEvent.change(screen.getByLabelText('Yeni Veritabanı Adı'), { target: { value: 'CorrectedDB' } })
    fireEvent.click(screen.getByRole('button', { name: /Ön Kontrolü Çalıştır/i }))
    await waitFor(() => expect(RestoreApi.startRestorePrecheck).toHaveBeenCalledTimes(2))
    const calls = vi.mocked(RestoreApi.startRestorePrecheck).mock.calls
    expect(calls[1][2].databaseName).toBe('CorrectedDB')
    expect(calls[1][3]).not.toBe(calls[0][3])
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

    // Step 1: where
    expect(await screen.findByRole('combobox', { name: 'Yedekleme bilgisayarı' })).toHaveValue('agent-1')
    expect(stepperCurrent()).toMatch(/Nereye/)
    const continueBtn = screen.getByRole('button', { name: /Devam et/i })
    expect(continueBtn).toBeEnabled()
    fireEvent.click(continueBtn)

    // Step 2: how
    expect(await screen.findByRole('heading', { name: /Nasıl geri yüklensin/i })).toBeInTheDocument()
    expect(stepperCurrent()).toMatch(/Nasıl/)
    const precheckBtn = screen.getByRole('button', { name: /Ön Kontrolü Çalıştır/i })
    expect(precheckBtn).toBeEnabled()
    fireEvent.click(precheckBtn)

    // Step 3: check
    await waitFor(() => expect(RestoreApi.startRestorePrecheck).toHaveBeenCalled())
    expect(RestoreApi.startRestorePrecheck).toHaveBeenCalledWith('tenant-a', 'artifact-1', expect.objectContaining({
      agentId: 'agent-1', target: sampleTargets.targets[0].target, databaseName: 'SalesDB_restored', mode: 'NewDatabase',
    }), expect.any(String))
    expect(await screen.findByText(/Tüm ön kontroller başarıyla tamamlandı/i)).toBeInTheDocument()
    expect(stepperCurrent()).toMatch(/Kontrol/)
    expect(screen.queryByRole('button', { name: /Geri yüklemeyi başlat/i })).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: /Devam et/i }))

    // Step 4: confirm
    expect(stepperCurrent()).toMatch(/Onay/)
    const executeBtn = screen.getByRole('button', { name: /Geri yüklemeyi başlat/i })
    expect(executeBtn).toBeEnabled()
    fireEvent.click(executeBtn)

    await waitFor(() => expect(RestoreApi.startRestore).toHaveBeenCalled())
    expect(RestoreApi.startRestore).toHaveBeenCalledWith('tenant-a', 'artifact-1', expect.objectContaining({
      agentId: 'agent-1', target: sampleTargets.targets[0].target, precheckId: 'precheck-op-1',
    }), expect.any(String))
  })

  it('blocks an ineligible agent without sending a precheck and allows an eligible selection', async () => {
    renderWithClient(<Routes><Route path="/tenants/:tenantId/artifacts/:id/restore" element={<RestoreWizardPage />} /></Routes>, '/tenants/tenant-a/artifacts/artifact-1/restore')
    const selector = await screen.findByRole('combobox', { name: 'Yedekleme bilgisayarı' })
    fireEvent.change(selector, { target: { value: 'agent-2' } })
    const next = screen.getByRole('button', { name: /Devam et/i })
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
    renderWithClient(wizardRoutes, wizardUrl)
    fireEvent.click(await screen.findByRole('button', { name: /Devam et/i }))
    fireEvent.click(screen.getByRole('button', { name: /Ön Kontrolü Çalıştır/i }))
    expect(await screen.findByRole('alert')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /Devam et/i })).toBeDisabled()
    expect(screen.queryByRole('button', { name: /Geri yüklemeyi başlat/i })).not.toBeInTheDocument()
    expect(RestoreApi.startRestore).not.toHaveBeenCalled()
  })

  it('keeps identifiers out of the way: they are collapsed by default and copyable', async () => {
    renderWithClient(wizardRoutes, wizardUrl)
    await screen.findByRole('combobox', { name: 'Yedekleme bilgisayarı' })
    const sections = screen.getAllByText('Teknik ayrıntılar').map(summary => summary.closest('details'))
    expect(sections.length).toBeGreaterThan(0)
    for (const section of sections) expect(section).not.toHaveAttribute('open')
    expect(screen.getByText('artifact-1')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /Kopyala: Yedek dosyası kimliği/ })).toBeInTheDocument()
  })

  it('does not let a check that no longer matches the settings start the restore', async () => {
    const prechecks = { 'precheck-1': passedPrecheck('precheck-1'), 'precheck-2': passedPrecheck('precheck-2') }
    vi.mocked(RestoreApi.startRestorePrecheck).mockResolvedValueOnce(prechecks['precheck-1']).mockResolvedValueOnce(prechecks['precheck-2'])
    vi.mocked(RestoreApi.getRestore).mockImplementation((_tenantId, id) => Promise.resolve(prechecks[id as keyof typeof prechecks] ?? sampleRestore))
    vi.mocked(RestoreApi.startRestore).mockResolvedValue(sampleRestore)
    renderWithClient(wizardRoutes, wizardUrl)
    fireEvent.click(await screen.findByRole('button', { name: /Devam et/i }))
    fireEvent.click(screen.getByRole('button', { name: /Ön Kontrolü Çalıştır/i }))
    await screen.findByRole('button', { name: /Devam et/i })
    fireEvent.click(screen.getByRole('button', { name: /^← Geri/i }))

    // Changing a setting discards the check: the only way forward is to run it again.
    fireEvent.change(screen.getByLabelText('Yeni Veritabanı Adı'), { target: { value: 'CorrectedDB' } })
    expect(screen.queryByRole('button', { name: /Devam et/i })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Geri yüklemeyi başlat/i })).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: /Ön Kontrolü Çalıştır/i }))
    await waitFor(() => expect(RestoreApi.startRestorePrecheck).toHaveBeenCalledTimes(2))
    fireEvent.click(await screen.findByRole('button', { name: /Devam et/i }))
    fireEvent.click(await screen.findByRole('button', { name: /Geri yüklemeyi başlat/i }))
    await waitFor(() => expect(RestoreApi.startRestore).toHaveBeenCalledTimes(1))
    expect(RestoreApi.startRestore).toHaveBeenCalledWith('tenant-a', 'artifact-1', expect.objectContaining({ databaseName: 'CorrectedDB', precheckId: 'precheck-2' }), expect.any(String))
  })

  it('explains new versus replacing in plain words and blocks the replacement until the exact database name is typed', async () => {
    vi.mocked(RestoreApi.startRestoreDiscovery).mockResolvedValue({ ...sampleRestore, id: 'discovery-1' })
    vi.mocked(RestoreApi.startRestorePrecheck).mockResolvedValue(passedPrecheck('precheck-replace'))
    vi.mocked(RestoreApi.getRestore).mockImplementation((_tenantId, id) => Promise.resolve(id === 'precheck-replace' ? passedPrecheck(id) : sampleRestore))
    vi.mocked(RestoreApi.startRestore).mockResolvedValue(sampleRestore)
    renderWithClient(wizardRoutes, wizardUrl)
    fireEvent.click(await screen.findByRole('button', { name: /Devam et/i }))

    const newCard = screen.getByRole('button', { name: /Yeni bir veritabanı olarak/i })
    expect(newCard).toHaveAccessibleDescription(/dokunulmaz/i)
    fireEvent.click(screen.getByRole('button', { name: /Mevcut bir veritabanının yerine/i }))
    await waitFor(() => expect(RestoreApi.startRestoreDiscovery).toHaveBeenCalledTimes(1))
    expect(screen.getByText(/Veri kaybı riski/i)).toBeInTheDocument()

    // Nothing has to be typed to check the restore; the name is asked for once, right before the replacement starts.
    fireEvent.click(screen.getByRole('button', { name: /Ön Kontrolü Çalıştır/i }))
    fireEvent.click(await screen.findByRole('button', { name: /Devam et/i }))
    fireEvent.click(await screen.findByRole('button', { name: /Geri yüklemeyi başlat/i }))
    expect(RestoreApi.startRestore).not.toHaveBeenCalled()

    const dialog = await screen.findByRole('dialog')
    const confirm = within(dialog).getByRole('button', { name: /Üzerine yaz ve geri yükle/i })
    const input = within(dialog).getByRole('textbox')
    expect(confirm).toBeDisabled()
    for (const wrong of ['wrong-db-name', 'salesdb_restored', 'SalesDB_restored ']) {
      fireEvent.change(input, { target: { value: wrong } })
      expect(confirm).toBeDisabled()
    }
    fireEvent.click(confirm)
    expect(RestoreApi.startRestore).not.toHaveBeenCalled()

    fireEvent.change(input, { target: { value: 'SalesDB_restored' } })
    expect(confirm).toBeEnabled()
    fireEvent.click(confirm)
    await waitFor(() => expect(RestoreApi.startRestore).toHaveBeenCalledTimes(1))
    expect(RestoreApi.startRestore).toHaveBeenCalledWith('tenant-a', 'artifact-1', expect.objectContaining({
      mode: 'ReplaceExistingDatabase', databaseName: 'SalesDB_restored', confirmOverwrite: 'SalesDB_restored', precheckId: 'precheck-replace',
    }), expect.any(String))
  })

  it('starts a new database without asking for a typed name', async () => {
    vi.mocked(RestoreApi.startRestorePrecheck).mockResolvedValue(passedPrecheck('precheck-new'))
    vi.mocked(RestoreApi.getRestore).mockImplementation((_tenantId, id) => Promise.resolve(id === 'precheck-new' ? passedPrecheck(id) : sampleRestore))
    vi.mocked(RestoreApi.startRestore).mockResolvedValue(sampleRestore)
    renderWithClient(wizardRoutes, wizardUrl)
    await reachConfirmStep()
    fireEvent.click(screen.getByRole('button', { name: /Geri yüklemeyi başlat/i }))
    await waitFor(() => expect(RestoreApi.startRestore).toHaveBeenCalledTimes(1))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(vi.mocked(RestoreApi.startRestore).mock.calls[0][2].confirmOverwrite).toBeUndefined()
  })

  describe('when starting the restore fails', () => {
    beforeEach(() => {
      vi.mocked(RestoreApi.startRestorePrecheck).mockResolvedValue(passedPrecheck('precheck-retry'))
      vi.mocked(RestoreApi.getRestore).mockImplementation((_tenantId, id) => Promise.resolve(id === 'precheck-retry' ? passedPrecheck(id) : sampleRestore))
    })
    it('retries with the same idempotency key after an interrupted request, so no second restore is created', async () => {
      vi.mocked(RestoreApi.startRestore).mockRejectedValueOnce(new TypeError('network down')).mockResolvedValueOnce(sampleRestore)
      renderWithClient(wizardRoutes, wizardUrl)
      await reachConfirmStep()
      fireEvent.click(screen.getByRole('button', { name: /Geri yüklemeyi başlat/i }))
      const retry = await screen.findByRole('button', { name: /Tekrar dene/i })
      expect(await screen.findByRole('alert')).toBeInTheDocument()
      fireEvent.click(retry)
      await waitFor(() => expect(RestoreApi.startRestore).toHaveBeenCalledTimes(2))
      const calls = vi.mocked(RestoreApi.startRestore).mock.calls
      expect(calls[1][3]).toBe(calls[0][3])
      expect(calls[1][2]).toEqual(calls[0][2])
    })
    it('uses a new idempotency key after the server rejected the request', async () => {
      vi.mocked(RestoreApi.startRestore).mockRejectedValueOnce(new ApiError(409, { code: 'restore_conflict' })).mockResolvedValueOnce(sampleRestore)
      renderWithClient(wizardRoutes, wizardUrl)
      await reachConfirmStep()
      fireEvent.click(screen.getByRole('button', { name: /Geri yüklemeyi başlat/i }))
      expect(await screen.findByRole('alert')).toBeInTheDocument()
      fireEvent.click(screen.getByRole('button', { name: /Geri yüklemeyi başlat/i }))
      await waitFor(() => expect(RestoreApi.startRestore).toHaveBeenCalledTimes(2))
      const calls = vi.mocked(RestoreApi.startRestore).mock.calls
      expect(calls[1][3]).not.toBe(calls[0][3])
    })
    it('keeps the same key when a replacement is retried from its confirmation dialog', async () => {
      vi.mocked(RestoreApi.startRestoreDiscovery).mockResolvedValue({ ...sampleRestore, id: 'discovery-1' })
      vi.mocked(RestoreApi.startRestore).mockRejectedValueOnce(new TypeError('network down')).mockResolvedValueOnce(sampleRestore)
      renderWithClient(wizardRoutes, wizardUrl)
      await reachConfirmStep('replace')
      fireEvent.click(screen.getByRole('button', { name: /Geri yüklemeyi başlat/i }))
      const dialog = await screen.findByRole('dialog')
      fireEvent.change(within(dialog).getByRole('textbox'), { target: { value: 'SalesDB_restored' } })
      fireEvent.click(within(dialog).getByRole('button', { name: /Üzerine yaz ve geri yükle/i }))
      await waitFor(() => expect(within(dialog).getByRole('alert')).toBeInTheDocument())
      fireEvent.click(within(dialog).getByRole('button', { name: /Tekrar dene/i }))
      await waitFor(() => expect(RestoreApi.startRestore).toHaveBeenCalledTimes(2))
      const calls = vi.mocked(RestoreApi.startRestore).mock.calls
      expect(calls[1][3]).toBe(calls[0][3])
    })
  })
})

describe('file archive restore', () => {
  const passed: RestoreView = { ...sampleRestore, id: 'file-precheck', purpose: 'Precheck', status: 'Succeeded', options: { verifyChecksum: true, verifyBackup: true, files: { paths: null, relativeDirectory: '' } } }
  beforeEach(() => {
    vi.mocked(RestoreApi.getRestoreTargets).mockResolvedValue({ ...sampleTargets, engine: 'file-system' })
    vi.mocked(RestoreApi.startRestorePrecheck).mockResolvedValue(passed)
    vi.mocked(RestoreApi.getRestore).mockResolvedValue(passed)
  })

  it('shows persisted file selections in restore history and detail instead of database mode labels', async () => {
    const fileRestore: RestoreView = { ...sampleRestore, options: { verifyChecksum: true, verifyBackup: true, files: { paths: ['reports', 'notes.txt'], relativeDirectory: 'recovery' } } }
    vi.mocked(RestoreApi.getRestores).mockResolvedValue({ ...sampleRestorePage, items: [fileRestore] })
    const list = renderWithClient(<RestoresPage />)
    expect(await screen.findByText('Dosya / klasör geri yükleme')).toBeInTheDocument()
    expect(screen.getByText('recovery')).toBeInTheDocument()
    list.unmount()
    vi.mocked(RestoreApi.getRestore).mockResolvedValue(fileRestore)
    renderWithClient(<Routes><Route path="/tenants/:tenantId/restores/:id" element={<RestoreDetailPage />} /></Routes>, '/tenants/tenant-a/restores/restore-1')
    await screen.findByRole('heading', { name: /Geri yükleme · RestoredSalesDB/ })
    fireEvent.click(screen.getByRole('tab', { name: 'Kaynak ve seçenekler' }))
    expect(screen.getByText('reports, notes.txt')).toBeInTheDocument()
    expect(screen.getByText('recovery')).toBeInTheDocument()
    expect(screen.queryByText('Etkin bağlantıları sonlandır')).not.toBeInTheDocument()
  })

  const fileWizard = <Routes><Route path="/tenants/:tenantId/artifacts/:id/restore" element={<RestoreWizardPage />} /><Route path="/tenants/:tenantId/restores/:id" element={<p>File restore started</p>} /></Routes>
  const runCheck = () => fireEvent.click(screen.getByRole('button', { name: /Ön Kontrolü Çalıştır/ }))
  const field = (name: string) => screen.getByLabelText(name)
  /** Walks an eligible file wizard through where, how and check up to the confirmation step. */
  async function reachFileConfirmStep(overwrite = false) {
    fireEvent.click(await screen.findByRole('button', { name: /Devam et/ }))
    if (overwrite) fireEvent.click(screen.getByRole('checkbox', { name: 'Mevcut dosyaların üzerine yazmaya izin ver' }))
    runCheck()
    fireEvent.click(await screen.findByRole('button', { name: /Devam et/ }))
    await screen.findByRole('button', { name: 'Dosyaları geri yükle' })
  }

  it('follows the same where / how / check / confirm steps as the database restore', async () => {
    renderWithClient(fileWizard, wizardUrl)
    await screen.findByRole('combobox', { name: 'Yedekleme bilgisayarı' })
    expect(stepperCurrent()).toMatch(/Nereye/)
    fireEvent.click(screen.getByRole('button', { name: /Devam et/ }))
    expect(stepperCurrent()).toMatch(/Nasıl/)
    expect(screen.queryByRole('button', { name: 'Dosyaları geri yükle' })).not.toBeInTheDocument()
    runCheck()
    await screen.findByText(/Tüm ön kontroller başarıyla tamamlandı/)
    expect(stepperCurrent()).toMatch(/Kontrol/)
    expect(screen.queryByRole('button', { name: 'Dosyaları geri yükle' })).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: /Devam et/ }))
    expect(stepperCurrent()).toMatch(/Onay/)
    expect(RestoreApi.startRestore).not.toHaveBeenCalled()
  })

  it('blocks an ineligible agent from going past the first step', async () => {
    renderWithClient(fileWizard, wizardUrl)
    const agent = await screen.findByRole('combobox', { name: 'Yedekleme bilgisayarı' })
    fireEvent.change(agent, { target: { value: '' } })
    expect(screen.getByRole('button', { name: /Devam et/ })).toBeDisabled()
    expect(RestoreApi.startRestorePrecheck).not.toHaveBeenCalled()
  })

  it('restores the whole directory to its original location, asking for the exact reference only right before the overwrite starts', async () => {
    renderWithClient(fileWizard, wizardUrl)
    fireEvent.click(await screen.findByRole('radio', { name: 'Özgün konum' }))
    expect(screen.queryByLabelText('Hedef göreli klasör')).not.toBeInTheDocument()
    expect(screen.queryByLabelText('Veritabanı parolası')).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: /Devam et/ }))
    fireEvent.click(screen.getByRole('checkbox', { name: 'Mevcut dosyaların üzerine yazmaya izin ver' }))
    // Nothing has to be typed to check the restore.
    expect(screen.getByRole('button', { name: /Ön Kontrolü Çalıştır/ })).toBeEnabled()
    runCheck()
    fireEvent.click(await screen.findByRole('button', { name: /Devam et/ }))
    expect(RestoreApi.startRestorePrecheck).toHaveBeenCalledWith('tenant-a', 'artifact-1', expect.objectContaining({
      agentId: 'agent-1', databaseName: 'SalesDB', mode: 'ReplaceExistingDatabase',
      target: expect.objectContaining({ host: 'localhost', port: null, integratedSecurity: true, databaseCredentialReference: null }),
      options: { terminateActiveConnections: false, verifyChecksum: true, verifyBackup: true, files: { paths: null, relativeDirectory: '' } },
    }), expect.any(String))
    fireEvent.click(screen.getByRole('button', { name: 'Dosyaları geri yükle' }))
    expect(RestoreApi.startRestore).not.toHaveBeenCalled()
    const dialog = await screen.findByRole('dialog')
    const confirm = within(dialog).getByRole('button', { name: /Üzerine yaz ve geri yükle/ })
    const input = within(dialog).getByRole('textbox')
    expect(confirm).toBeDisabled()
    for (const wrong of ['wrong', 'salesdb', 'SalesDB ']) {
      fireEvent.change(input, { target: { value: wrong } })
      expect(confirm).toBeDisabled()
    }
    fireEvent.change(input, { target: { value: 'SalesDB' } })
    fireEvent.click(confirm)
    await waitFor(() => expect(RestoreApi.startRestore).toHaveBeenCalledWith('tenant-a', 'artifact-1', expect.objectContaining({
      precheckId: 'file-precheck', confirmOverwrite: 'SalesDB', options: expect.objectContaining({ files: { paths: null, relativeDirectory: '' } }),
    }), expect.any(String)))
    expect(await screen.findByText('File restore started')).toBeInTheDocument()
  })

  it('starts a restore that overwrites nothing without asking for a typed name', async () => {
    renderWithClient(fileWizard, wizardUrl)
    await reachFileConfirmStep()
    fireEvent.click(screen.getByRole('button', { name: 'Dosyaları geri yükle' }))
    await waitFor(() => expect(RestoreApi.startRestore).toHaveBeenCalledTimes(1))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(vi.mocked(RestoreApi.startRestore).mock.calls[0][2].confirmOverwrite).toBeUndefined()
  })

  it('restores only selected paths under an alternate root and discards a check that no longer matches the settings', async () => {
    renderWithClient(fileWizard, wizardUrl)
    fireEvent.change(await screen.findByRole('textbox', { name: 'Hedef dosya kaynağı referansı' }), { target: { value: 'recovery' } })
    fireEvent.change(field('Hedef göreli klasör'), { target: { value: '../escape' } })
    expect(screen.getByRole('button', { name: /Devam et/ })).toBeDisabled()
    fireEvent.change(field('Hedef göreli klasör'), { target: { value: 'restored/2026' } })
    fireEvent.click(screen.getByRole('button', { name: /Devam et/ }))
    fireEvent.click(screen.getByRole('radio', { name: 'Seçili dosya ve klasörler' }))
    expect(screen.getByRole('button', { name: /Ön Kontrolü Çalıştır/ })).toBeDisabled()
    fireEvent.change(field('Geri yüklenecek göreli yollar'), { target: { value: 'reports\nnotes.txt' } })
    runCheck()
    await screen.findByRole('button', { name: /Devam et/ })
    expect(RestoreApi.startRestorePrecheck).toHaveBeenCalledWith('tenant-a', 'artifact-1', expect.objectContaining({
      databaseName: 'recovery', mode: 'NewDatabase', options: expect.objectContaining({ files: { paths: ['reports', 'notes.txt'], relativeDirectory: 'restored/2026' } }),
    }), expect.any(String))

    // Changing what is restored discards the check: the only way forward is to run it again.
    fireEvent.click(screen.getByRole('button', { name: /^← Geri/ }))
    fireEvent.change(field('Geri yüklenecek göreli yollar'), { target: { value: 'other.txt' } })
    expect(screen.queryByRole('button', { name: /Devam et/ })).not.toBeInTheDocument()
    runCheck()
    fireEvent.click(await screen.findByRole('button', { name: /Devam et/ }))
    fireEvent.click(screen.getByRole('button', { name: 'Dosyaları geri yükle' }))
    await waitFor(() => expect(RestoreApi.startRestore).toHaveBeenCalledWith('tenant-a', 'artifact-1', expect.objectContaining({
      databaseName: 'recovery', precheckId: 'file-precheck', options: expect.objectContaining({ files: { paths: ['other.txt'], relativeDirectory: 'restored/2026' } }),
    }), expect.any(String)))
  })

  it('keeps the same idempotency key when a precheck request has an uncertain outcome', async () => {
    vi.mocked(RestoreApi.startRestorePrecheck).mockRejectedValueOnce(new Error('timeout')).mockResolvedValue(passed)
    renderWithClient(fileWizard, wizardUrl)
    fireEvent.click(await screen.findByRole('button', { name: /Devam et/ }))
    const check = screen.getByRole('button', { name: /Ön Kontrolü Çalıştır/ })
    fireEvent.click(check)
    await waitFor(() => expect(check).toBeEnabled())
    fireEvent.click(check)
    await waitFor(() => expect(RestoreApi.startRestorePrecheck).toHaveBeenCalledTimes(2))
    const calls = vi.mocked(RestoreApi.startRestorePrecheck).mock.calls
    expect(calls[1][3]).toBe(calls[0][3])
  })

  it('uses a new precheck key after the settings changed', async () => {
    vi.mocked(RestoreApi.startRestorePrecheck).mockRejectedValueOnce(new Error('timeout')).mockResolvedValue(passed)
    renderWithClient(fileWizard, wizardUrl)
    fireEvent.click(await screen.findByRole('button', { name: /Devam et/ }))
    runCheck()
    await waitFor(() => expect(RestoreApi.startRestorePrecheck).toHaveBeenCalledTimes(1))
    fireEvent.click(screen.getByRole('radio', { name: 'Seçili dosya ve klasörler' }))
    fireEvent.change(field('Geri yüklenecek göreli yollar'), { target: { value: 'notes.txt' } })
    runCheck()
    await waitFor(() => expect(RestoreApi.startRestorePrecheck).toHaveBeenCalledTimes(2))
    const calls = vi.mocked(RestoreApi.startRestorePrecheck).mock.calls
    expect(calls[1][3]).not.toBe(calls[0][3])
  })

  describe('when starting the restore fails', () => {
    it('retries with the same idempotency key after an interrupted request, so no second restore is created', async () => {
      vi.mocked(RestoreApi.startRestore).mockRejectedValueOnce(new TypeError('network down')).mockResolvedValueOnce(sampleRestore)
      renderWithClient(fileWizard, wizardUrl)
      await reachFileConfirmStep()
      fireEvent.click(screen.getByRole('button', { name: 'Dosyaları geri yükle' }))
      const retry = await screen.findByRole('button', { name: /Tekrar dene/i })
      expect(await screen.findByRole('alert')).toBeInTheDocument()
      fireEvent.click(retry)
      await waitFor(() => expect(RestoreApi.startRestore).toHaveBeenCalledTimes(2))
      const calls = vi.mocked(RestoreApi.startRestore).mock.calls
      expect(calls[1][3]).toBe(calls[0][3])
      expect(calls[1][2]).toEqual(calls[0][2])
    })
    it('uses a new idempotency key after the server rejected the request', async () => {
      vi.mocked(RestoreApi.startRestore).mockRejectedValueOnce(new ApiError(409, { code: 'restore_conflict' })).mockResolvedValueOnce(sampleRestore)
      renderWithClient(fileWizard, wizardUrl)
      await reachFileConfirmStep()
      fireEvent.click(screen.getByRole('button', { name: 'Dosyaları geri yükle' }))
      expect(await screen.findByRole('alert')).toBeInTheDocument()
      fireEvent.click(screen.getByRole('button', { name: 'Dosyaları geri yükle' }))
      await waitFor(() => expect(RestoreApi.startRestore).toHaveBeenCalledTimes(2))
      const calls = vi.mocked(RestoreApi.startRestore).mock.calls
      expect(calls[1][3]).not.toBe(calls[0][3])
    })
    it('keeps the same key when an overwrite is retried from its confirmation dialog', async () => {
      vi.mocked(RestoreApi.startRestore).mockRejectedValueOnce(new TypeError('network down')).mockResolvedValueOnce(sampleRestore)
      renderWithClient(fileWizard, wizardUrl)
      await reachFileConfirmStep(true)
      fireEvent.click(screen.getByRole('button', { name: 'Dosyaları geri yükle' }))
      const dialog = await screen.findByRole('dialog')
      fireEvent.change(within(dialog).getByRole('textbox'), { target: { value: 'SalesDB' } })
      fireEvent.click(within(dialog).getByRole('button', { name: /Üzerine yaz ve geri yükle/ }))
      await waitFor(() => expect(within(dialog).getByRole('alert')).toBeInTheDocument())
      fireEvent.click(within(dialog).getByRole('button', { name: /Tekrar dene/i }))
      await waitFor(() => expect(RestoreApi.startRestore).toHaveBeenCalledTimes(2))
      const calls = vi.mocked(RestoreApi.startRestore).mock.calls
      expect(calls[1][3]).toBe(calls[0][3])
    })
  })

  it('keeps identifiers collapsed and copyable on every step', async () => {
    renderWithClient(fileWizard, wizardUrl)
    await screen.findByRole('combobox', { name: 'Yedekleme bilgisayarı' })
    const collapsed = () => screen.getAllByText('Teknik ayrıntılar').map(summary => summary.closest('details'))
    expect(collapsed().length).toBeGreaterThan(0)
    for (const section of collapsed()) expect(section).not.toHaveAttribute('open')
    expect(screen.getByRole('button', { name: /Kopyala: Yedek dosyası kimliği/ })).toBeInTheDocument()
    await reachFileConfirmStep()
    for (const section of collapsed()) expect(section).not.toHaveAttribute('open')
    expect(screen.getByRole('button', { name: /Kopyala: Yedekleme bilgisayarı kimliği/ })).toBeInTheDocument()
  })

  it('disables original location when its source-agent mapping is unavailable', async () => {
    vi.mocked(RestoreApi.getRestoreTargets).mockResolvedValue({ ...sampleTargets, engine: 'file-system', targets: [] })
    renderWithClient(fileWizard, wizardUrl)
    expect(await screen.findByRole('radio', { name: 'Özgün konum' })).toBeDisabled()
    expect(screen.getByRole('radio', { name: 'Alternatif konum' })).toBeChecked()
  })
})

describe('precheck verdict', () => {
  const check = (checkId: string, status: 'Passed' | 'Warning' | 'Failed' | 'Skipped', reasonCode: string) => ({ checkId, status, reasonCode, message: 'English backend message.' })
  const withChecks = (status: RestoreView['status'], preCheck: RestoreResult['preCheck']): RestoreView => ({
    ...sampleRestore, id: 'verdict', purpose: 'Precheck', status,
    result: { checksumVerified: true, backupVerified: true, databaseState: null, targetStateNote: null, preCheck, warnings: [], sourceBackup: null, targetServer: null, fileMappings: [], startedAt: null, finishedAt: null, restoreStartedAt: null, restoreFinishedAt: null },
  })
  const rawId = /\b(server|restore|staging|permission|database)\.[a-z-]+/
  /** Visible text is whatever lies outside the collapsed technical section. */
  const visibleRawIds = () => [...document.body.querySelectorAll('*')].filter(node => node.children.length === 0 && rawId.test(node.textContent ?? '') && !node.closest('details'))

  beforeEach(() => vi.mocked(RestoreApi.getRestoreTargets).mockResolvedValue({ ...sampleTargets, engine: 'file-system' }))

  async function showVerdict(view: RestoreView) {
    vi.mocked(RestoreApi.startRestorePrecheck).mockResolvedValue(view)
    vi.mocked(RestoreApi.getRestore).mockResolvedValue(view)
    renderWithClient(<Routes><Route path="/tenants/:tenantId/artifacts/:id/restore" element={<RestoreWizardPage />} /></Routes>, wizardUrl)
    fireEvent.click(await screen.findByRole('button', { name: /Devam et/ }))
    fireEvent.click(screen.getByRole('button', { name: /Ön Kontrolü Çalıştır/ }))
    await waitFor(() => expect(stepperCurrent()).toMatch(/Kontrol/))
  }

  it('names every check in plain words and keeps raw check ids in the collapsed technical details, copyable', async () => {
    await showVerdict(withChecks('Succeeded', [check('server.reachable', 'Passed', 'ok'), check('restore.target-sessions', 'Warning', 'sessions_open'), check('some.future-check', 'Passed', 'ok')]))
    await screen.findByRole('button', { name: /Kopyala: server.reachable/ })
    expect(visibleRawIds()).toEqual([])
    expect(screen.queryByText('some.future-check')?.closest('details')).not.toHaveAttribute('open')
    expect(screen.getByText('restore.target-sessions').closest('details')).not.toHaveAttribute('open')
    for (const id of ['restore.target-sessions', 'some.future-check']) expect(screen.getByRole('button', { name: `Kopyala: ${id}` })).toBeInTheDocument()
    expect(screen.getAllByRole('row').length).toBeGreaterThanOrEqual(4)
  })

  it('explains a failed check in plain words without exposing its reason code', async () => {
    await showVerdict(withChecks('Failed', [check('restore.disk-space', 'Failed', 'InsufficientDiskSpace')]))
    await screen.findByRole('button', { name: /Kopyala: restore.disk-space/ })
    expect(visibleRawIds()).toEqual([])
    for (const node of screen.queryAllByText(/InsufficientDiskSpace/)) expect(node.closest('details')).not.toBeNull()
    expect(screen.getAllByText('Hedefte yeterli disk alanı yok.').length).toBeGreaterThan(0)
    expect(screen.getByRole('button', { name: /Devam et/ })).toBeDisabled()
  })
})
