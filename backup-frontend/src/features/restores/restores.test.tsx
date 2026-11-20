import { cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { render } from '@/test/render'
import { ApiError } from '@/lib/api/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { BackupArtifact } from '@/features/artifacts/types'
import { RestoresPage } from '@/pages/restores/restores-page'
import { RestoreDetailPage } from '@/pages/restores/restore-detail-page'
import { RestoreWizardPage } from '@/pages/restores/restore-wizard-page'
import * as RestoreApi from './api'
import * as AgentApi from '@/features/agents/api'
import * as ArtifactApi from '@/features/artifacts/api'
import * as BackupApi from '@/features/backups/api'
import { makeRun } from '@/features/backups/test-data'
import type { RestorePage, RestoreResult, RestoreTargetsResponse, RestoreView } from './types'
import { LocationProbe } from '@/features/platform/location-probe'
import { PrecheckResults } from './precheck-results'
import { describeFailure } from '@/lib/api/failure-messages'

const tenancy = vi.hoisted(() => ({ tenantId: 'tenant-a' }))
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

vi.mock('@/features/backups/api', async (importOriginal) => ({ ...await importOriginal<typeof BackupApi>(), getBackup: vi.fn() }))
vi.mock('@/features/artifacts/api', () => ({
  getArtifact: vi.fn(),
  listArtifacts: vi.fn(),
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

const hrArtifact: BackupArtifact = { ...sampleArtifact, id: 'artifact-2', databaseName: 'HRDB', jobName: 'Nightly HR' }

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

const listRestores = (page: RestorePage) => vi.mocked(RestoreApi.getRestores).mockResolvedValue(page)

beforeEach(() => {
  tenancy.tenantId = 'tenant-a'

  listRestores(sampleRestorePage)
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
  vi.mocked(ArtifactApi.listArtifacts).mockResolvedValue({ items: [sampleArtifact, hrArtifact], totalCount: 2, page: 1, pageSize: 100 })
  vi.mocked(BackupApi.getBackup).mockResolvedValue(makeRun({ id: 'run-1', status: 'Succeeded', attempts: [{ ...makeRun().attempts[0], artifacts: [sampleArtifact] }] }))
  vi.mocked(RestoreApi.startRestorePrecheck).mockResolvedValue(sampleRestore)
  vi.mocked(RestoreApi.startRestore).mockResolvedValue(sampleRestore)
})

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

const technicalRegions = () => screen.queryAllByRole('region', { name: 'Teknik ayrıntılar' })
async function closeTechnical() {
  for (const trigger of screen.queryAllByRole('button', { name: 'Teknik ayrıntılar', expanded: true })) await userEvent.click(trigger)
  expect(technicalRegions()).toHaveLength(0)
}
/** Opens the technical-details sections in turn until one holds `text`; leaves it open. */
async function technicalWith(text: string | RegExp) {
  const count = screen.getAllByRole('button', { name: 'Teknik ayrıntılar' }).length
  for (let i = 0; i < count; i++) {
    await userEvent.click(screen.getAllByRole('button', { name: 'Teknik ayrıntılar' })[i])
    const region = technicalRegions().find(node => typeof text === 'string' ? node.textContent?.includes(text) : text.test(node.textContent ?? ''))
    if (region) return within(region)
    await closeTechnical()
  }
  throw new Error(`No technical details contain ${String(text)}`)
}

describe('RestoresPage', () => {
  it('renders restore history list and filters', async () => {
    renderWithClient(<RestoresPage />)
    expect(await screen.findByText('RestoredSalesDB')).toBeInTheDocument()
    expect(screen.getByText("Yeni veritabanı")).toBeInTheDocument()
    expect(screen.getAllByText('Çalışıyor').length).toBeGreaterThan(0)
  })

  it('sends URL search, sorting and paging in one all-purpose request', async () => {
    listRestores({ ...sampleRestorePage, totalCount: 75 })
    renderWithClient(<RestoresPage />, '/?search=sales&status=Failed&sort=target&dir=asc&page=2')
    await waitFor(() => expect(RestoreApi.getRestores).toHaveBeenLastCalledWith('tenant-a', expect.objectContaining({ purpose: 'All', search: 'sales', status: 'Failed', sort: 'target', dir: 'asc', page: 2, pageSize: 25 }), expect.anything()))
    expect(screen.getByRole('searchbox', { name: 'Ara' })).toHaveValue('sales')
    fireEvent.click(await screen.findByRole('button', { name: 'Sonraki' }))
    await waitFor(() => expect(RestoreApi.getRestores).toHaveBeenLastCalledWith('tenant-a', expect.objectContaining({ page: 3 }), expect.anything()))
  })

  it('never shows or asks for a raw backup file id', async () => {
    renderWithClient(<RestoresPage />)
    await screen.findByText('RestoredSalesDB')
    expect(screen.queryByText('artifact-1')).not.toBeInTheDocument()
    expect(screen.queryByRole('textbox')).not.toBeInTheDocument()
    expect(screen.queryByLabelText(/ID/)).not.toBeInTheDocument()
  })

  it('filters by a backup found through the picker by its name', async () => {
    renderWithClient(<RestoresPage />)
    await screen.findByText('RestoredSalesDB')
    expect(screen.getByLabelText('Yedek dosyası')).not.toBeVisible()
    await userEvent.click(screen.getByText('Diğer filtreler'))
    await userEvent.type(screen.getByRole('combobox', { name: 'Yedek dosyası' }), 'hr')
    expect(within(screen.getByRole('listbox')).getAllByRole('option')).toHaveLength(1)
    await userEvent.click(screen.getByRole('option', { name: /HRDB/ }))
    await waitFor(() => expect(RestoreApi.getRestores).toHaveBeenLastCalledWith('tenant-a', expect.objectContaining({ artifactId: 'artifact-2' }), expect.anything()))
    expect(screen.getByRole('combobox', { name: 'Yedek dosyası' })).toHaveDisplayValue(/HRDB/)
  })

  it('filters by computer name while preserving the selected backup and status', async () => {
    renderWithClient(<RestoresPage />, '/?artifactId=artifact-2&status=Failed')
    await screen.findByText('RestoredSalesDB')
    await userEvent.type(screen.getByRole('combobox', { name: 'Yedekleme bilgisayarı' }), 'SQL')
    await userEvent.click(await screen.findByRole('option', { name: 'SQL Server Agent 1' }))
    await waitFor(() => expect(RestoreApi.getRestores).toHaveBeenLastCalledWith('tenant-a', expect.objectContaining({ agentId: 'agent-1', artifactId: 'artifact-2', status: 'Failed' }), expect.anything()))
  })

  it('names the filtered backup even when it is older than the ones the picker loaded', async () => {
    vi.mocked(ArtifactApi.getArtifact).mockResolvedValue({ ...sampleArtifact, id: 'artifact-9', databaseName: 'OldDB' })
    renderWithClient(<RestoresPage />, '/?artifactId=artifact-9')
    await waitFor(() => expect(screen.getByRole('combobox', { name: 'Yedek dosyası' })).toHaveDisplayValue(/OldDB/))
    expect(RestoreApi.getRestores).toHaveBeenCalledWith('tenant-a', expect.objectContaining({ artifactId: 'artifact-9' }), expect.anything())
  })

  it('shows plain text, not a dead link, while the backup run is unknown', async () => {
    vi.mocked(ArtifactApi.getArtifact).mockReturnValue(new Promise(() => {}))
    listRestores({ ...sampleRestorePage, items: [{ ...sampleRestore, sourceDatabaseName: 'SalesDB', sourceJobName: 'Nightly Sales Backup' }] })
    renderWithClient(<RestoresPage />)
    expect(await screen.findByText('SalesDB')).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: /^SalesDB/ })).not.toBeInTheDocument()
  })

  it('names the source backup and the backup computer instead of showing ids', async () => {
    listRestores({ ...sampleRestorePage, items: [{ ...sampleRestore, sourceDatabaseName: 'SalesDB', sourceJobName: 'Nightly Sales Backup' }] })
    renderWithClient(<RestoresPage />)
    expect(await screen.findByRole('link', { name: /^SalesDB/ })).toHaveAttribute('href', '/tenants/tenant-a/backup-runs/run-1')
    expect(within(screen.getByRole('table')).getByText('SQL Server Agent 1')).toBeInTheDocument()
  })

  it('titles a verification trial by the backup\'s database, tells the sources apart by date, and explains the untouched target', async () => {
    const unnamed = { ...sampleArtifact, databaseName: null }
    vi.mocked(ArtifactApi.getArtifact).mockResolvedValue(unnamed)
    vi.mocked(BackupApi.getBackup).mockResolvedValue(makeRun({ id: 'run-1', status: 'Succeeded', attempts: [{ ...makeRun().attempts[0], artifacts: [unnamed] }] }))
    vi.mocked(RestoreApi.getRestores).mockResolvedValue({ ...sampleRestorePage, items: [{ ...sampleRestore, id: 'trial-1', purpose: 'Verification', databaseName: 'bp_verify_a042480e4a9c408f96a1852a850514fc', status: 'Succeeded', targetState: 'NotModified', sourceDatabaseName: null }] })
    renderWithClient(<RestoresPage />)
    expect(await screen.findByRole('link', { name: 'Doğrulama denemesi: Uygulama' })).toHaveAttribute('href', '/tenants/tenant-a/restores/trial-1')
    expect(screen.queryByText(/bp_verify_/)).not.toBeInTheDocument()
    expect(await screen.findByRole('link', { name: /^Uygulama · .*2026/ })).toHaveAttribute('href', '/tenants/tenant-a/backup-runs/run-1')
    expect(screen.getByText('Hedef veritabanı değiştirilmedi')).toBeInTheDocument()
    expect(screen.queryByText('Değiştirilmedi')).not.toBeInTheDocument()
  })

  it('names a verification trial on its page and keeps the temporary database name in the technical details', async () => {
    const trial: RestoreView = { ...sampleRestore, purpose: 'Verification', databaseName: 'bp_verify_abc', sourceDatabaseName: 'SalesDB' }
    vi.mocked(RestoreApi.getRestore).mockResolvedValue(trial)
    renderWithClient(<Routes><Route path="/tenants/:tenantId/restores/:id" element={<RestoreDetailPage />} /></Routes>, '/tenants/tenant-a/restores/restore-1')
    expect(await screen.findByRole('heading', { level: 1, name: 'Doğrulama denemesi: SalesDB' })).toBeInTheDocument()
    expect(screen.queryByText('bp_verify_abc')).not.toBeInTheDocument()
    expect((await technicalWith('bp_verify_abc')).getByText('bp_verify_abc')).toBeInTheDocument()
  })

  describe('when there is nothing to list', () => {
    beforeEach(() => listRestores({ items: [], totalCount: 0, page: 1, pageSize: 25 }))
    const emptyActions = () => screen.queryAllByRole('link').concat(screen.queryAllByRole('button'))

    it('says why it is empty and offers one way forward: opening the backups to restore one', async () => {
      renderWithClient(<RestoresPage />)
      expect(await screen.findByText('Henüz geri yükleme yapılmadı. Bir yedeğin ayrıntısından geri yükleyebilirsiniz.')).toBeInTheDocument()
      const actions = emptyActions()
      expect(actions).toHaveLength(1)
      expect(actions[0]).toHaveAttribute('href', '/tenants/tenant-a/backup-runs')
    })

    it('clears an empty filtered history from the filters and then offers the backup files', async () => {
      renderWithClient(<RestoresPage />, '/?status=Failed')
      await screen.findByText('Bu filtrelere uyan geri yükleme yok.')
      fireEvent.click(screen.getByRole('button', { name: 'Filtreleri temizle' }))
      await waitFor(() => expect(RestoreApi.getRestores).toHaveBeenLastCalledWith('tenant-a', expect.objectContaining({ purpose: 'All', page: 1 }), expect.anything()))
      expect(screen.queryByRole('button', { name: 'Filtreleri temizle' })).not.toBeInTheDocument()
      expect(await screen.findByRole('link', { name: 'Yedek dosyalarını aç' })).toHaveAttribute('href', '/tenants/tenant-a/backup-runs')
    })
  })
})

describe('RestoresPage search, sorting and paging', () => {
  const lastQuery = () => vi.mocked(RestoreApi.getRestores).mock.lastCall?.[1]
  const location = () => screen.getByTestId('location')
  const header = (name: string) => screen.findByRole('columnheader', { name })
  const mountAt = async (url: string) => {
    renderWithClient(<><RestoresPage /><LocationProbe /></>, url)
    await screen.findByText('RestoredSalesDB')
  }

  it('asks for the newest restores first without sending a sort, and marks the time column as the sorted one', async () => {
    await mountAt('/')
    expect(lastQuery()).toEqual({ page: 1, pageSize: 25, purpose: 'All' })
    expect(await header('Ne zaman')).toHaveAttribute('aria-sort', 'descending')
    for (const name of ['Hedef veritabanı', 'Durum', 'Kaynak yedek', 'Yedekleme bilgisayarı']) expect(await header(name)).toHaveAttribute('aria-sort', 'none')
  })

  it.each([
    ['Hedef veritabanı', 'target', 'asc'],
    ['Durum', 'status', 'asc'],
    ['Kaynak yedek', 'source', 'asc'],
    ['Yedekleme bilgisayarı', 'computer', 'asc'],
  ])('the %s header sorts by %s, %s first, and the next click reverses it', async (name, field, first) => {
    listRestores({ ...sampleRestorePage, totalCount: 75 })
    await mountAt('/?page=2')
    fireEvent.click(within(await header(name)).getByRole('button'))
    await waitFor(() => expect(lastQuery()).toEqual({ page: 1, pageSize: 25, purpose: 'All', sort: field, dir: first }))
    expect(await header(name)).toHaveAttribute('aria-sort', 'ascending')
    expect(location()).toHaveTextContent(`/?sort=${field}&dir=${first}`)
    fireEvent.click(within(await header(name)).getByRole('button'))
    await waitFor(() => expect(lastQuery()).toMatchObject({ sort: field, dir: 'desc' }))
    expect(await header(name)).toHaveAttribute('aria-sort', 'descending')
  })

  it('returning to the default order drops the sort from the request and the URL', async () => {
    await mountAt('/?sort=createdAt&dir=asc')
    expect(lastQuery()).toMatchObject({ sort: 'createdAt', dir: 'asc' })
    fireEvent.click(within(await header('Ne zaman')).getByRole('button'))
    await waitFor(() => expect(lastQuery()).toEqual({ page: 1, pageSize: 25, purpose: 'All' }))
    expect(location()).toHaveTextContent(/^\/$/)
  })

  it('searches the restores, keeps the filters and goes back to the first page', async () => {
    listRestores({ ...sampleRestorePage, totalCount: 75 })
    await mountAt('/?status=Failed&page=2')
    fireEvent.change(screen.getByRole('searchbox', { name: 'Ara' }), { target: { value: '  satış  ' } })
    await waitFor(() => expect(lastQuery()).toEqual({ page: 1, pageSize: 25, purpose: 'All', status: 'Failed', search: 'satış' }))
    expect(location()).toHaveTextContent(/^\/\?status=Failed&search=\+\+sat%C4%B1%C5%9F\+\+$/)
  })

  it('restores the search, the sort and the page from the URL and ignores an unknown sort', async () => {
    await mountAt('/?search=sales&sort=computer&dir=desc&page=3')
    expect(lastQuery()).toEqual({ page: 3, pageSize: 25, purpose: 'All', search: 'sales', sort: 'computer', dir: 'desc' })
    expect(screen.getByRole('searchbox', { name: 'Ara' })).toHaveValue('sales')
    expect(await header('Yedekleme bilgisayarı')).toHaveAttribute('aria-sort', 'descending')
    cleanup()
    await mountAt('/?sort=databaseName&dir=sideways')
    expect(lastQuery()).toEqual({ page: 1, pageSize: 25, purpose: 'All' })
  })

  it('pages with the search and sort in place, and a new sort goes back to page 1', async () => {
    listRestores({ ...sampleRestorePage, totalCount: 75 })
    await mountAt('/?search=sales&sort=status&dir=asc')
    fireEvent.click(screen.getByRole('button', { name: 'Sonraki' }))
    await waitFor(() => expect(lastQuery()).toEqual({ page: 2, pageSize: 25, purpose: 'All', search: 'sales', sort: 'status', dir: 'asc' }))
    expect(location()).toHaveTextContent('search=sales&sort=status&dir=asc&page=2')
    fireEvent.click(within(await header('Kaynak yedek')).getByRole('button'))
    await waitFor(() => expect(lastQuery()).toEqual({ page: 1, pageSize: 25, purpose: 'All', search: 'sales', sort: 'source', dir: 'asc' }))
  })

  it('says nothing matched the search and clears it, keeping the sort', async () => {
    listRestores({ items: [], totalCount: 0, page: 1, pageSize: 25 })
    renderWithClient(<><RestoresPage /><LocationProbe /></>, '/?search=yok&sort=target&dir=asc')
    expect(await screen.findByText('Bu filtrelere uyan geri yükleme yok.')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Filtreleri temizle' }))
    await waitFor(() => expect(lastQuery()).toEqual({ page: 1, pageSize: 25, purpose: 'All', sort: 'target', dir: 'asc' }))
    expect(location()).toHaveTextContent(/^\/\?sort=target&dir=asc$/)
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
    const cancelButton = screen.getByRole('button', { name: /Geri yüklemeyi iptal et/i })
    expect(cancelButton).toBeEnabled()

    fireEvent.click(cancelButton)
    expect(await screen.findByText(/Geri Yükleme iptal edilsin mi\?/i)).toBeInTheDocument()
  })

  it('sends the cancel request only after the operator confirms it, then says it was requested', async () => {
    vi.mocked(RestoreApi.cancelRestore).mockResolvedValue({ ...sampleRestore, cancellationRequestedAt: '2026-10-08T12:12:00Z' })
    renderWithClient(detailRoutes, '/tenants/tenant-a/restores/restore-1')
    fireEvent.click(await screen.findByRole('button', { name: /Geri yüklemeyi iptal et/i }))
    expect(RestoreApi.cancelRestore).not.toHaveBeenCalled()
    fireEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'İptali onayla' }))
    await waitFor(() => expect(RestoreApi.cancelRestore).toHaveBeenCalledWith('tenant-a', 'restore-1'))
    expect(await screen.findByText(/İptal isteği iletildi/)).toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('keeps the cancel dialog open with the reason when the request fails', async () => {
    vi.mocked(RestoreApi.cancelRestore).mockRejectedValue(new ApiError(409, { code: 'restore_conflict' }))
    renderWithClient(detailRoutes, '/tenants/tenant-a/restores/restore-1')
    fireEvent.click(await screen.findByRole('button', { name: /Geri yüklemeyi iptal et/i }))
    fireEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'İptali onayla' }))
    expect(await within(screen.getByRole('dialog')).findByRole('alert')).toBeInTheDocument()
  })

  it('links back to the restores list by its name and shows the source backup once, not again under the options', async () => {
    vi.mocked(RestoreApi.getRestore).mockResolvedValue(failed)
    renderWithClient(detailRoutes, detailUrl)
    expect(await screen.findByRole('link', { name: 'Geri yüklemeler' })).toHaveAttribute('href', '/tenants/tenant-a/restores')
    expect(await screen.findAllByRole('link', { name: /^SalesDB/ })).toHaveLength(1)
    fireEvent.click(screen.getByRole('tab', { name: 'Seçenekler' }))
    expect(screen.queryByRole('link', { name: /^SalesDB/ })).not.toBeInTheDocument()
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

  const detailRoutes = <Routes><Route path="/tenants/:tenantId/restores/:id" element={<RestoreDetailPage />} /></Routes>
  const detailUrl = '/tenants/tenant-a/restores/restore-1'
  /** Visible text is whatever lies outside the collapsed technical section. */
  const visibleLeaves = (text: string) => [...document.body.querySelectorAll('*')].filter(node => node.children.length === 0 && node.textContent?.includes(text) && !node.closest('[role="region"][aria-label="Teknik ayrıntılar"]'))
  const failed: RestoreView = { ...sampleRestore, status: 'Failed', targetState: 'PartiallyRestored', failureCode: 'SqlRestoreFailed', sourceDatabaseName: 'SalesDB', sourceJobName: 'Nightly Sales Backup', completedAt: '2026-10-08T12:20:00Z' }

  it('names the source backup and the backup computer, and never shows ids or codes outside the technical details', async () => {
    vi.mocked(RestoreApi.getRestore).mockResolvedValue(failed)
    renderWithClient(detailRoutes, detailUrl)
    expect(await screen.findByRole('link', { name: /^SalesDB/ })).toHaveAttribute('href', '/tenants/tenant-a/backup-runs/run-1')
    expect(screen.getByText('SQL Server Agent 1')).toBeInTheDocument()
    expect(screen.getByText(/Kısmen geri yüklendi/)).toBeInTheDocument()
    for (const raw of ['restore-1', 'artifact-1', 'agent-1', 'replica-1', 'PartiallyRestored', 'SqlRestoreFailed', 'localhost']) expect(visibleLeaves(raw), raw).toEqual([])
  })

  it('keeps the technical details collapsed and copyable as one block', async () => {
    vi.mocked(RestoreApi.getRestore).mockResolvedValue(failed)
    renderWithClient(detailRoutes, detailUrl)
    await screen.findByRole('link', { name: /^SalesDB/ })
    expect(screen.getAllByRole('button', { name: 'Teknik ayrıntılar' }).length).toBeGreaterThan(0)
    expect(technicalRegions()).toHaveLength(0)
    for (const label of ['Geri yükleme kimliği', 'Yedek dosyası kimliği', 'Yedekleme bilgisayarı kimliği', 'Yedek kopyası kimliği', 'Hedef durumu kodu', 'Hedef sunucu']) {
      expect(screen.queryByRole('button', { name: 'Kopyala' }), label).not.toBeInTheDocument()
      const details = await technicalWith(`${label}:`)
      expect(details.getByRole('button', { name: 'Kopyala' }), label).toBeInTheDocument()
      await closeTechnical()
    }
  })

  it('tells a failed restore in one plain sentence without the raw code', async () => {
    vi.mocked(RestoreApi.getRestore).mockResolvedValue(failed)
    renderWithClient(detailRoutes, detailUrl)
    const notice = await screen.findByRole('alert')
    expect(within(notice).queryByText('SqlRestoreFailed')).not.toBeInTheDocument()
    expect(screen.queryByText('SqlRestoreFailed')).not.toBeInTheDocument()
    expect((await technicalWith('SqlRestoreFailed')).getByText('SqlRestoreFailed')).toBeInTheDocument()
  })

  it('keeps source safety options and attempt progress accessible in their tabs', async () => {
    renderWithClient(detailRoutes, detailUrl)
    expect(await screen.findByRole('heading', { name: /Geri yükleme · RestoredSalesDB/i })).toBeInTheDocument()
    fireEvent.click(screen.getByRole('tab', { name: 'Seçenekler' }))
    expect(screen.getByText("Etkin bağlantıları sonlandır")).toBeInTheDocument()
    expect(screen.queryByText("Geri yükleme için silmeye karşı korunuyor")).not.toBeInTheDocument()
    expect((await technicalWith("Geri yükleme için silmeye karşı korunuyor")).getByText("Geri yükleme için silmeye karşı korunuyor")).toBeVisible()
    await closeTechnical()
    fireEvent.click(screen.getByRole('tab', { name: 'Denemeler' }))
    expect(screen.getByRole('article', { name: '1. deneme' })).toBeInTheDocument()
    expect(screen.getByRole('progressbar', { name: '1. deneme ilerlemesi' })).toHaveAttribute('aria-valuenow', '65.5')
    fireEvent.click(screen.getByRole('tab', { name: 'Doğrulama sonucu' }))
    expect(screen.getByText(/Doğrulama sonucu henüz bildirilmedi/)).toBeInTheDocument()
  })

  it('names every reported check in plain words and keeps engine ids and warning codes in the technical details', async () => {
    vi.mocked(RestoreApi.getRestore).mockResolvedValue({
      ...sampleRestore, status: 'Succeeded',
      result: { checksumVerified: true, backupVerified: true, databaseState: 'ONLINE', targetStateNote: null, preCheck: [{ checkId: 'restore.disk-space', status: 'Passed', reasonCode: 'ok', message: 'Enough.' }], warnings: [{ code: 'SomeWarning', message: 'x' }], sourceBackup: null, targetServer: null, fileMappings: [], startedAt: null, finishedAt: null, restoreStartedAt: null, restoreFinishedAt: null },
    })
    renderWithClient(detailRoutes, detailUrl)
    await screen.findByRole('heading', { name: /Geri yükleme · RestoredSalesDB/i })
    fireEvent.click(screen.getByRole('tab', { name: 'Doğrulama sonucu' }))
    expect(screen.getByText('Yeterli disk alanı var')).toBeInTheDocument()
    for (const raw of ['restore.disk-space', 'SomeWarning']) expect(visibleLeaves(raw), raw).toEqual([])
    for (const raw of ['restore.disk-space', 'SomeWarning']) {
      expect(screen.queryByText(raw), raw).not.toBeInTheDocument()
      expect((await technicalWith(raw)).getByRole('button', { name: 'Kopyala' })).toBeInTheDocument()
      await closeTechnical()
    }
  })
})

const wizardRoutes = <Routes><Route path="/tenants/:tenantId/backup-runs/:id/restore" element={<RestoreWizardPage />} /></Routes>
const wizardUrl = '/tenants/tenant-a/backup-runs/run-1/restore'
const passedPrecheck = (id: string): RestoreView => ({ ...sampleRestore, id, purpose: 'Precheck', status: 'Succeeded' })
/** The progress indicator's current step: the one numbering scheme the wizard shows. */
const stepperCurrent = () => screen.getByRole('listitem', { current: 'step' }).textContent ?? ''
const stepLabels = () => within(screen.getByRole('navigation', { name: 'Adımlar' })).getAllByRole('listitem').map((item) => item.textContent?.replace(/^\d+/, '').replace(/ \((?:tamamlandı|geçerli adım)\)$/, ''))
const next = () => screen.findByRole('button', { name: /Devam et/i })
const startButton = () => screen.findByRole('button', { name: /Geri yüklemeyi başlat/i })
/** Walks an eligible wizard through where and how; the pre-check runs by itself on continuing. */
async function reachConfirmStep(how: 'new' | 'replace' = 'new') {
  const continueButton = await next()
  if (how === 'replace') {
    fireEvent.click(screen.getByRole('button', { name: /Yerine geç/i }))
    await waitFor(() => expect(RestoreApi.startRestoreDiscovery).toHaveBeenCalled())
  }
  fireEvent.click(continueButton)
  await startButton()
}
const twoEligibleAgents: RestoreTargetsResponse = { ...sampleTargets, agents: [sampleTargets.agents[0], { ...sampleTargets.agents[0], agentId: 'agent-3', hostName: 'SQL Server Agent 3' }] }
const sentPrecheck = () => vi.mocked(RestoreApi.startRestorePrecheck).mock.calls[0][2]

describe('RestoreWizardPage', () => {
  beforeEach(() => {
    vi.mocked(RestoreApi.startRestorePrecheck).mockResolvedValue(passedPrecheck('precheck-1'))
    vi.mocked(RestoreApi.getRestore).mockImplementation((_tenantId, id) => Promise.resolve(id === 'precheck-1' ? passedPrecheck(id) : sampleRestore))
    vi.mocked(RestoreApi.startRestoreDiscovery).mockResolvedValue({ ...sampleRestore, id: 'discovery-1' })
  })

  it('names the page after what it restores and links back to the backup file', async () => {
    const view = renderWithClient(wizardRoutes, wizardUrl)
    expect(await screen.findByRole('heading', { level: 1, name: 'Veritabanını geri yükle' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Yedekleme denemeleri' })).toHaveAttribute('href', '/tenants/tenant-a/backup-runs')
    expect(screen.getByRole('link', { name: /^SalesDB/ })).toHaveAttribute('href', '/tenants/tenant-a/backup-runs/run-1')
    view.unmount()
    vi.mocked(RestoreApi.getRestoreTargets).mockResolvedValue({ ...sampleTargets, engine: 'file-system' })
    renderWithClient(wizardRoutes, wizardUrl)
    expect(await screen.findByRole('heading', { level: 1, name: 'Dosyaları geri yükle' })).toBeInTheDocument()
  })

  it('has two screens, Nereye ve nasıl and Onay, when only one computer fits, and asks for the computer first when several do', async () => {
    const view = renderWithClient(wizardRoutes, wizardUrl)
    await next()
    expect(screen.queryByRole('combobox', { name: 'Yedekleme bilgisayarı' })).not.toBeInTheDocument()
    expect(stepLabels()).toEqual(['Nereye ve nasıl', 'Onay'])
    expect(stepperCurrent()).toMatch(/Nereye ve nasıl/)
    view.unmount()
    vi.mocked(RestoreApi.getRestoreTargets).mockResolvedValue(twoEligibleAgents)
    renderWithClient(wizardRoutes, wizardUrl)
    expect(await screen.findByRole('combobox', { name: 'Yedekleme bilgisayarı' })).toBeInTheDocument()
    expect(stepLabels()).toEqual(['Kaynak', 'Nereye ve nasıl', 'Onay'])
    fireEvent.click(await next())
    expect(stepperCurrent()).toMatch(/Nereye ve nasıl/)
  })

  it('defaults to the backup\'s own server and a new database, with the replacement marked risky', async () => {
    renderWithClient(wizardRoutes, wizardUrl)
    await next()
    expect(screen.getByRole('radio', { name: /Yedeğin alındığı sunucu \(localhost:1433\)/ })).toBeChecked()
    expect(screen.queryByLabelText('Ana Makine / Veritabanı Sunucusu')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: /Yeni veritabanı/ })).toHaveAttribute('aria-pressed', 'true')
    expect(screen.getByLabelText('Yeni Veritabanı Adı')).toHaveValue('SalesDB_restored')
    expect(screen.getByRole('button', { name: /Yerine geç.*riskli/ })).toHaveAttribute('aria-pressed', 'false')
  })

  it('asks only for the address of another server, and for a login only when SQL Server needs one', async () => {
    renderWithClient(wizardRoutes, wizardUrl)
    await next()
    fireEvent.click(screen.getByRole('radio', { name: 'Başka sunucu' }))
    expect(screen.getByLabelText('Ana Makine / Veritabanı Sunucusu')).toBeInTheDocument()
    for (const gone of [/Bağlantı Noktası/, /Windows kimlik doğrulaması/, /Şifrele/, /Sertifika/]) expect(screen.queryByLabelText(gone)).not.toBeInTheDocument()
    expect(screen.queryByRole('checkbox', { name: /Sunucu Sertifikasına Güven/ })).not.toBeInTheDocument()
    expect(screen.queryByLabelText('Veritabanı kullanıcı adı')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: /Devam et/ })).toBeDisabled()
    fireEvent.click(screen.getByRole('checkbox', { name: 'Kullanıcı adı ve parola gerekiyor' }))
    expect(screen.getByLabelText('Veritabanı kullanıcı adı')).toBeInTheDocument()
  })

  it.each([
    { engine: 'mssql' as const, how: 'new' as const, terminate: false, target: { host: 'other-sql', port: null, integratedSecurity: true, databaseCredentialReference: null, encrypt: true, trustServerCertificate: false } },
    { engine: 'mssql' as const, how: 'replace' as const, terminate: true, target: { host: 'other-sql', port: null, integratedSecurity: true, databaseCredentialReference: null, encrypt: true, trustServerCertificate: false } },
  ])('sends the job constants for another $engine server ($how)', async ({ how, terminate, target }) => {
    renderWithClient(wizardRoutes, wizardUrl)
    await next()
    fireEvent.click(screen.getByRole('radio', { name: 'Başka sunucu' }))
    fireEvent.change(screen.getByLabelText('Ana Makine / Veritabanı Sunucusu'), { target: { value: 'other-sql' } })
    if (how === 'replace') {
      fireEvent.click(screen.getByRole('button', { name: /Yerine geç/i }))
      await waitFor(() => expect(RestoreApi.startRestoreDiscovery).toHaveBeenCalled())
    }
    fireEvent.click(screen.getByRole('button', { name: /Devam et/i }))
    await startButton()
    expect(sentPrecheck()).toEqual({
      agentId: 'agent-1', target, databaseName: how === 'replace' ? 'SalesDB' : 'SalesDB_restored',
      mode: how === 'replace' ? 'ReplaceExistingDatabase' : 'NewDatabase',
      options: { terminateActiveConnections: terminate, verifyChecksum: true, verifyBackup: true },
    })
    expect(vi.mocked(RestoreApi.startRestoreDiscovery).mock.calls.every((call) => call[2].target.port === null && call[2].target.trustServerCertificate === false)).toBe(true)
  })

  it('sends the pinned PostgreSQL values for another server and rejects identifiers over 63 UTF-8 bytes', async () => {
    vi.mocked(RestoreApi.getRestoreTargets).mockResolvedValue({ ...sampleTargets, engine: 'postgres', targets: [] })
    renderWithClient(wizardRoutes, wizardUrl)
    const host = await screen.findByLabelText("PostgreSQL Sunucusu")
    expect(screen.getByText('Yedek, hedef PostgreSQL sunucusuna geri yüklenir.')).toBeInTheDocument()
    expect(screen.getByRole('radio', { name: 'Başka sunucu' })).toBeChecked()
    expect(screen.getByRole('radio', { name: /Yedeğin alındığı sunucu/ })).toBeDisabled()
    expect(screen.queryByLabelText(/Bağlantı Noktası/)).not.toBeInTheDocument()
    const run = screen.getByRole('button', { name: /Devam et/i })
    expect(run).toBeDisabled()
    fireEvent.change(screen.getByLabelText("Veritabanı kullanıcı adı"), { target: { value: 'pg_user' } })
    fireEvent.change(screen.getByLabelText("Veritabanı parolası"), { target: { value: 'pg-secret' } })
    fireEvent.click(screen.getByRole('button', { name: 'Girişi kaydet' }))
    expect(await screen.findByText('Sunucu adresini girin.')).toBeInTheDocument()
    expect(RestoreApi.storeDatabaseLogin).not.toHaveBeenCalled()
    expect(screen.queryByText('Bu hedef için kayıtlı bir veritabanı girişi seçili.')).not.toBeInTheDocument()
    fireEvent.change(host, { target: { value: 'pg-target' } })
    vi.mocked(RestoreApi.storeDatabaseLogin).mockResolvedValue({ reference: 'pg-restore' })
    fireEvent.change(screen.getByLabelText("Veritabanı kullanıcı adı"), { target: { value: 'pg_user' } })
    fireEvent.change(screen.getByLabelText("Veritabanı parolası"), { target: { value: 'pg-secret' } })
    expect(run).toBeDisabled()
    expect(screen.getByText(/henüz kaydedilmedi/)).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Girişi kaydet' }))
    await waitFor(() => expect(RestoreApi.storeDatabaseLogin).toHaveBeenCalledWith('tenant-a', expect.any(String), { userName: 'pg_user', password: 'pg-secret' }))
    await waitFor(() => expect(run).toBeEnabled())
    fireEvent.change(screen.getByLabelText('Yeni Veritabanı Adı'), { target: { value: '数'.repeat(22) } })
    expect(run).toBeDisabled()
    fireEvent.change(screen.getByLabelText('Yeni Veritabanı Adı'), { target: { value: '数'.repeat(21) } })
    fireEvent.click(run)
    await waitFor(() => expect(RestoreApi.startRestorePrecheck).toHaveBeenCalledTimes(1))
    expect(sentPrecheck()).toEqual(expect.objectContaining({
      databaseName: '数'.repeat(21),
      target: { host: 'pg-target', port: 5432, integratedSecurity: false, databaseCredentialReference: 'pg-restore', encrypt: true, trustServerCertificate: false },
      options: { terminateActiveConnections: false, verifyChecksum: true, verifyBackup: true },
    }))
  })

  it('has no certificate trust box up front and no verification options anywhere in the form', async () => {
    renderWithClient(wizardRoutes, wizardUrl)
    await next()
    fireEvent.click(screen.getByRole('radio', { name: 'Başka sunucu' }))
    fireEvent.click(screen.getByRole('button', { name: /Yerine geç/i }))
    expect(screen.getAllByRole('checkbox')).toHaveLength(2) // "Kullanıcı adı ve parola gerekiyor" and "Bağlantıyı şifrele"
    expect(screen.getByRole('checkbox', { name: 'Bağlantıyı şifrele' })).toBeChecked()
    expect(screen.queryByText(/Sertifika/i)).not.toBeInTheDocument()
    expect(screen.queryByText(/Açık bağlantıları kapat$/)).not.toBeInTheDocument()
    expect(screen.queryByText(/RESTORE VERIFYONLY|SHA-256/)).not.toBeInTheDocument()
  })

  it('runs the pre-check by itself on continuing and shows its result on Onay, with the source and the exact target', async () => {
    renderWithClient(wizardRoutes, wizardUrl)
    expect(screen.queryByRole('button', { name: /Ön Kontrolü Çalıştır/i })).not.toBeInTheDocument()
    await reachConfirmStep()
    expect(RestoreApi.startRestorePrecheck).toHaveBeenCalledTimes(1)
    expect(stepperCurrent()).toMatch(/Onay/)
    expect(await screen.findByText(/Tüm ön kontroller başarıyla tamamlandı/i)).toBeInTheDocument()
    const source = screen.getByRole('region', { name: 'Yedek dosyası bilgisi' })
    expect(within(source).getByText(/SalesDB/)).toBeInTheDocument()
    expect(within(source).getByText('1 kopyanın bütünlüğü doğrulandı.')).toBeInTheDocument()
    expect(screen.getByText('localhost:1433')).toBeInTheDocument()
    expect(screen.getByText('SalesDB_restored')).toBeInTheDocument()
    expect(screen.queryByText('artifact-1')).not.toBeInTheDocument()
    expect((await technicalWith('artifact-1')).getAllByText('artifact-1')).toHaveLength(1)
  })

  it('does not reach Onay when the pre-check fails, and says why in the error pattern', async () => {
    const failed: RestoreView = { ...sampleRestore, id: 'failed-precheck', purpose: 'Precheck', status: 'Failed', failureCode: 'StorageUnavailable', reason: null }
    vi.mocked(RestoreApi.startRestorePrecheck).mockResolvedValue(failed)
    vi.mocked(RestoreApi.getRestore).mockResolvedValue(failed)
    renderWithClient(wizardRoutes, wizardUrl)
    fireEvent.click(await next())
    expect(await screen.findByText(/Ön kontrol başarısız oldu/)).toBeInTheDocument()
    expect(screen.getAllByRole('alert').length).toBeGreaterThan(0)
    expect(stepperCurrent()).toMatch(/Nereye ve nasıl/)
    expect(screen.queryByRole('button', { name: /Geri yüklemeyi başlat/i })).not.toBeInTheDocument()
    expect(RestoreApi.startRestore).not.toHaveBeenCalled()
  })

  it('offers the closed "trust anyway" step only for an untrusted certificate, and trusts it for this restore only', async () => {
    const untrusted: RestoreView = { ...sampleRestore, id: 'untrusted-precheck', purpose: 'Precheck', status: 'Failed', failureCode: 'ServerCertificateUntrusted' }
    vi.mocked(RestoreApi.startRestorePrecheck).mockResolvedValueOnce(untrusted).mockResolvedValue(passedPrecheck('precheck-1'))
    vi.mocked(RestoreApi.getRestore).mockImplementation((_tenantId, id) => Promise.resolve(id === 'precheck-1' ? passedPrecheck(id) : untrusted))
    const view = renderWithClient(wizardRoutes, wizardUrl)
    await next()
    fireEvent.click(screen.getByRole('radio', { name: 'Başka sunucu' }))
    fireEvent.change(screen.getByLabelText('Ana Makine / Veritabanı Sunucusu'), { target: { value: 'other-sql' } })
    fireEvent.click(screen.getByRole('button', { name: /Devam et/i }))
    const summary = await screen.findByText('Bu sunucunun sertifikasına yine de güven')
    expect(screen.getByText('Bu sunucunun kimliği doğrulanamaz; yalnızca kendi sunucunuz olduğundan eminseniz açın.')).toBeInTheDocument()
    expect(summary.closest('details')).not.toHaveAttribute('open')
    expect(vi.mocked(RestoreApi.startRestorePrecheck).mock.calls[0][2].target.trustServerCertificate).toBe(false)
    fireEvent.click(summary)
    fireEvent.click(screen.getByRole('button', { name: 'Güven ve yeniden dene' }))
    await startButton()
    const trusted = { host: 'other-sql', port: null, integratedSecurity: true, databaseCredentialReference: null, encrypt: true, trustServerCertificate: true }
    expect(vi.mocked(RestoreApi.startRestorePrecheck).mock.calls[1][2].target).toEqual(trusted)
    fireEvent.click(screen.getByRole('button', { name: /Geri yüklemeyi başlat/i }))
    await waitFor(() => expect(RestoreApi.startRestore).toHaveBeenCalledWith('tenant-a', 'artifact-1', expect.objectContaining({ target: trusted, precheckId: 'precheck-1' }), expect.any(String)))
    view.unmount()
    renderWithClient(wizardRoutes, wizardUrl)
    await next()
    fireEvent.click(screen.getByRole('radio', { name: 'Başka sunucu' }))
    fireEvent.change(screen.getByLabelText('Ana Makine / Veritabanı Sunucusu'), { target: { value: 'other-sql' } })
    fireEvent.click(screen.getByRole('button', { name: /Devam et/i }))
    await waitFor(() => expect(RestoreApi.startRestorePrecheck).toHaveBeenCalledTimes(3))
    expect(vi.mocked(RestoreApi.startRestorePrecheck).mock.calls[2][2].target.trustServerCertificate).toBe(false)
  })

  it.each(['StorageUnavailable', 'AuthenticationFailed', 'DatabaseUnavailable', 'PreCheckFailed'])('does not offer to trust a certificate for the general %s failure', async (failureCode) => {
    const failed: RestoreView = { ...sampleRestore, id: 'failed-precheck', purpose: 'Precheck', status: 'Failed', failureCode }
    vi.mocked(RestoreApi.startRestorePrecheck).mockResolvedValue(failed)
    vi.mocked(RestoreApi.getRestore).mockResolvedValue(failed)
    renderWithClient(wizardRoutes, wizardUrl)
    fireEvent.click(await next())
    expect(await screen.findByText(/Ön kontrol başarısız oldu/)).toBeInTheDocument()
    expect(screen.queryByText('Bu sunucunun sertifikasına yine de güven')).not.toBeInTheDocument()
    expect(screen.queryByText(/yalnızca kendi sunucunuz olduğundan eminseniz/)).not.toBeInTheDocument()
  })

  describe('PostgreSQL', () => {
    const pgTargets: RestoreTargetsResponse = { ...sampleTargets, engine: 'postgres', targets: [] }
    beforeEach(() => vi.mocked(RestoreApi.getRestoreTargets).mockResolvedValue(pgTargets))
    const unnamed = { ...sampleArtifact, databaseName: null }
    const pgRun = () => makeRun({ id: 'run-1', status: 'Succeeded', attempts: [{ ...makeRun().attempts[0], artifacts: [unnamed] }] })

    async function fillAlternate() {
      fireEvent.change(await screen.findByLabelText('PostgreSQL Sunucusu'), { target: { value: 'pg-target' } })
      vi.mocked(RestoreApi.storeDatabaseLogin).mockResolvedValue({ reference: 'pg-restore' })
      fireEvent.change(screen.getByLabelText('Veritabanı kullanıcı adı'), { target: { value: 'pg_user' } })
      fireEvent.change(screen.getByLabelText('Veritabanı parolası'), { target: { value: 'pg-secret' } })
      fireEvent.click(screen.getByRole('button', { name: 'Girişi kaydet' }))
      await waitFor(() => expect(screen.getByRole('button', { name: /Devam et/i })).toBeEnabled())
    }

    it('names the database from the run when the artifact has no name, and explains the restore in two plain sentences with the full text collapsed', async () => {
      vi.mocked(BackupApi.getBackup).mockResolvedValue(pgRun())
      renderWithClient(wizardRoutes, wizardUrl)
      const source = await screen.findByRole('region', { name: 'Yedek dosyası bilgisi' })
      expect(within(source).getByText(/Uygulama/)).toBeInTheDocument()
      expect(within(source).queryByText(/Bilinmeyen veritabanı/)).not.toBeInTheDocument()
      expect(within(source).getByText('Yedek, seçtiğiniz veritabanına geri yüklenir. Mevcut bir veritabanının üzerine yazmak için ayrıca onay istenir.')).toBeInTheDocument()
      expect(screen.queryByText(/pg_restore içindeki SQL/)).not.toBeInTheDocument()
      fireEvent.click(within(source).getByRole('button', { name: 'Teknik ayrıntılar' }))
      expect(within(source).getByText(/pg_restore içindeki SQL/)).toBeInTheDocument()
      expect(within(source).getByText(/WAL\/PITR desteklenmez/)).toBeInTheDocument()
    })

    it('offers "Bağlantıyı şifrele" for another server (on by default) and sends the choice with the check', async () => {
      renderWithClient(wizardRoutes, wizardUrl)
      const encrypt = await screen.findByRole('checkbox', { name: 'Bağlantıyı şifrele' })
      expect(encrypt).toBeChecked()
      fireEvent.click(encrypt)
      await fillAlternate()
      fireEvent.click(screen.getByRole('button', { name: /Devam et/i }))
      await startButton()
      expect(sentPrecheck().target).toEqual({ host: 'pg-target', port: 5432, integratedSecurity: false, databaseCredentialReference: 'pg-restore', encrypt: false, trustServerCertificate: false })
    })

    it('trusts an unverifiable certificate for this restore only, on request', async () => {
      const untrusted: RestoreView = { ...sampleRestore, id: 'untrusted-precheck', purpose: 'Precheck', status: 'Failed', failureCode: 'ServerCertificateUntrusted' }
      vi.mocked(RestoreApi.startRestorePrecheck).mockResolvedValueOnce(untrusted).mockResolvedValue(passedPrecheck('precheck-1'))
      vi.mocked(RestoreApi.getRestore).mockImplementation((_tenantId, id) => Promise.resolve(id === 'precheck-1' ? passedPrecheck(id) : untrusted))
      renderWithClient(wizardRoutes, wizardUrl)
      await fillAlternate()
      fireEvent.click(screen.getByRole('button', { name: /Devam et/i }))
      fireEvent.click(await screen.findByText('Bu sunucunun sertifikasına yine de güven'))
      fireEvent.click(screen.getByRole('button', { name: 'Güven ve yeniden dene' }))
      await startButton()
      expect(vi.mocked(RestoreApi.startRestorePrecheck).mock.calls[0][2].target).toEqual(expect.objectContaining({ encrypt: true, trustServerCertificate: false }))
      expect(vi.mocked(RestoreApi.startRestorePrecheck).mock.calls[1][2].target).toEqual(expect.objectContaining({ encrypt: true, trustServerCertificate: true }))
    })

    const failedWith = (failureCode: string | null, reasonCode: string): RestoreView => ({
      ...sampleRestore, id: 'pg-failed', purpose: 'Precheck', status: 'Failed', failureCode,
      result: { checksumVerified: true, backupVerified: true, databaseState: null, targetStateNote: null, preCheck: [{ checkId: 'postgres.restore', status: 'Failed', reasonCode, message: 'English backend message.' }], warnings: [], sourceBackup: null, targetServer: null, fileMappings: [], startedAt: null, finishedAt: null, restoreStartedAt: null, restoreFinishedAt: null },
    })
    async function runFailed(view: RestoreView) {
      vi.mocked(RestoreApi.startRestorePrecheck).mockResolvedValue(view)
      vi.mocked(RestoreApi.getRestore).mockResolvedValue(view)
      renderWithClient(wizardRoutes, wizardUrl)
      await fillAlternate()
      fireEvent.click(screen.getByRole('button', { name: /Devam et/i }))
    }

    it('says the server offers no encrypted connection instead of asking to check the computer', async () => {
      await runFailed(failedWith('DatabaseUnavailable', 'server_tls_unavailable'))
      await screen.findByText(/Ön kontrol başarısız oldu/)
      expect(screen.getAllByText(describeFailure('server_tls_unavailable').what).length).toBeGreaterThan(0)
      expect(screen.queryByText(/Bilgisayarın açık ve ağa bağlı olduğundan emin olun/)).not.toBeInTheDocument()
    })

    it('shows the generic unexpected-failure text, never "geçici olarak kullanılamıyor", for an unmapped reason', async () => {
      await runFailed(failedWith('DatabaseUnavailable', 'some-new-reason'))
      await screen.findByText(/Ön kontrol başarısız oldu/)
      expect(screen.getAllByText(/Beklenmeyen bir sorun oluştu/).length).toBeGreaterThan(0)
      expect(screen.queryByText(/geçici olarak kullanılamıyor/)).not.toBeInTheDocument()
    })
  })

  it('shows the check running on the first screen until it ends', async () => {
    const running = { ...sampleRestore, id: 'running-precheck', purpose: 'Precheck' as const, status: 'Running' as const }
    vi.mocked(RestoreApi.startRestorePrecheck).mockResolvedValue(running)
    vi.mocked(RestoreApi.getRestore).mockResolvedValue(running)
    renderWithClient(wizardRoutes, wizardUrl)
    fireEvent.click(await next())
    expect(await screen.findByText('Ön kontrol çalışıyor…')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Geri yüklemeyi başlat/i })).not.toBeInTheDocument()
  })

  it('uses a new precheck idempotency key after correcting wizard input', async () => {
    renderWithClient(wizardRoutes, wizardUrl)
    await reachConfirmStep()
    fireEvent.click(screen.getByRole('button', { name: /^← Geri/i }))
    fireEvent.change(screen.getByLabelText('Yeni Veritabanı Adı'), { target: { value: 'CorrectedDB' } })
    fireEvent.click(screen.getByRole('button', { name: /Devam et/i }))
    await waitFor(() => expect(RestoreApi.startRestorePrecheck).toHaveBeenCalledTimes(2))
    const calls = vi.mocked(RestoreApi.startRestorePrecheck).mock.calls
    expect(calls[1][2].databaseName).toBe('CorrectedDB')
    expect(calls[1][3]).not.toBe(calls[0][3])
  })

  it.each(['CentralStorage', 'BackendRelayed', 'AgentLocal'] as const)('uses the eligible %s agent through precheck and execution', async (sourceAccess) => {
    vi.mocked(RestoreApi.getRestoreTargets).mockResolvedValue({
      ...sampleTargets,
      agents: [sampleTargets.agents[1], { ...sampleTargets.agents[0], sourceAccess }],
    })
    renderWithClient(wizardRoutes, wizardUrl)
    await reachConfirmStep()
    expect(RestoreApi.startRestorePrecheck).toHaveBeenCalledWith('tenant-a', 'artifact-1', expect.objectContaining({
      agentId: 'agent-1', target: sampleTargets.targets[0].target, databaseName: 'SalesDB_restored', mode: 'NewDatabase',
    }), expect.any(String))
    fireEvent.click(screen.getByRole('button', { name: /Geri yüklemeyi başlat/i }))
    await waitFor(() => expect(RestoreApi.startRestore).toHaveBeenCalled())
    expect(RestoreApi.startRestore).toHaveBeenCalledWith('tenant-a', 'artifact-1', expect.objectContaining({
      agentId: 'agent-1', target: sampleTargets.targets[0].target, precheckId: 'precheck-1',
    }), expect.any(String))
  })

  it('blocks an ineligible agent without sending a precheck and allows an eligible selection', async () => {
    vi.mocked(RestoreApi.getRestoreTargets).mockResolvedValue({ ...twoEligibleAgents, agents: [sampleTargets.agents[1], ...twoEligibleAgents.agents] })
    renderWithClient(wizardRoutes, wizardUrl)
    const selector = await screen.findByRole('combobox', { name: 'Yedekleme bilgisayarı' })
    fireEvent.change(selector, { target: { value: 'agent-2' } })
    const proceed = screen.getByRole('button', { name: /Devam et/i })
    expect(proceed).toBeDisabled()
    fireEvent.click(proceed)
    expect(RestoreApi.startRestorePrecheck).not.toHaveBeenCalled()
    expect(RestoreApi.startRestore).not.toHaveBeenCalled()
    fireEvent.change(selector, { target: { value: 'agent-1' } })
    expect(proceed).toBeEnabled()
  })

  it('keeps identifiers out of the way: they are collapsed by default and copyable', async () => {
    renderWithClient(wizardRoutes, wizardUrl)
    await next()
    expect(screen.getAllByRole('button', { name: 'Teknik ayrıntılar' }).length).toBeGreaterThan(0)
    expect(screen.queryByText('artifact-1')).not.toBeInTheDocument()
    const dialog = await technicalWith('artifact-1')
    expect(dialog.getByText('artifact-1')).toBeInTheDocument()
    expect(dialog.getByRole('button', { name: 'Kopyala' })).toBeInTheDocument()
  })

  it('does not let a check that no longer matches the settings start the restore', async () => {
    const prechecks = { 'precheck-1': passedPrecheck('precheck-1'), 'precheck-2': passedPrecheck('precheck-2') }
    vi.mocked(RestoreApi.startRestorePrecheck).mockResolvedValueOnce(prechecks['precheck-1']).mockResolvedValueOnce(prechecks['precheck-2'])
    vi.mocked(RestoreApi.getRestore).mockImplementation((_tenantId, id) => Promise.resolve(prechecks[id as keyof typeof prechecks] ?? sampleRestore))
    renderWithClient(wizardRoutes, wizardUrl)
    await reachConfirmStep()
    fireEvent.click(screen.getByRole('button', { name: /^← Geri/i }))

    // Changing a setting discards the check: continuing runs it again.
    fireEvent.change(screen.getByLabelText('Yeni Veritabanı Adı'), { target: { value: 'CorrectedDB' } })
    expect(screen.queryByRole('button', { name: /Geri yüklemeyi başlat/i })).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: /Devam et/i }))
    fireEvent.click(await startButton())
    await waitFor(() => expect(RestoreApi.startRestore).toHaveBeenCalledTimes(1))
    expect(RestoreApi.startRestorePrecheck).toHaveBeenCalledTimes(2)
    expect(RestoreApi.startRestore).toHaveBeenCalledWith('tenant-a', 'artifact-1', expect.objectContaining({ databaseName: 'CorrectedDB', precheckId: 'precheck-2' }), expect.any(String))
  })

  it('names the original database by default for Yerine geç, warns in plain words and blocks the replacement until the name is typed', async () => {
    vi.mocked(RestoreApi.startRestore).mockResolvedValue(sampleRestore)
    renderWithClient(wizardRoutes, wizardUrl)
    await next()
    expect(screen.getByRole('button', { name: /Yeni veritabanı/i })).toHaveAccessibleDescription(/dokunulmaz/i)
    fireEvent.click(screen.getByRole('button', { name: /Yerine geç/i }))
    await waitFor(() => expect(RestoreApi.startRestoreDiscovery).toHaveBeenCalledTimes(1))
    expect(screen.getByText(/Veri kaybı riski/i)).toBeInTheDocument()
    expect(screen.getByText('Açık bağlantılar kapatılacak ve mevcut veritabanının üzerine yazılacak.')).toBeInTheDocument()
    expect(screen.getByRole('textbox', { name: 'Yerine geçilecek veritabanı' })).toHaveValue('SalesDB')

    // Nothing has to be typed to check the restore; the name is asked for once, right before the replacement starts.
    fireEvent.click(screen.getByRole('button', { name: /Devam et/i }))
    fireEvent.click(await startButton())
    expect(RestoreApi.startRestore).not.toHaveBeenCalled()

    const dialog = await screen.findByRole('dialog')
    const confirm = within(dialog).getByRole('button', { name: /Üzerine yaz ve geri yükle/i })
    const input = within(dialog).getByRole('textbox')
    expect(confirm).toBeDisabled()
    for (const wrong of ['wrong-db-name', 'salesdb', 'SalesDB ']) {
      fireEvent.change(input, { target: { value: wrong } })
      expect(confirm).toBeDisabled()
    }
    fireEvent.click(confirm)
    expect(RestoreApi.startRestore).not.toHaveBeenCalled()

    fireEvent.change(input, { target: { value: 'SalesDB' } })
    expect(confirm).toBeEnabled()
    fireEvent.click(confirm)
    await waitFor(() => expect(RestoreApi.startRestore).toHaveBeenCalledTimes(1))
    expect(RestoreApi.startRestore).toHaveBeenCalledWith('tenant-a', 'artifact-1', expect.objectContaining({
      mode: 'ReplaceExistingDatabase', databaseName: 'SalesDB', confirmOverwrite: 'SalesDB', precheckId: 'precheck-1',
      options: { terminateActiveConnections: true, verifyChecksum: true, verifyBackup: true },
    }), expect.any(String))
  })

  it('starts a new database without asking for a typed name', async () => {
    vi.mocked(RestoreApi.startRestore).mockResolvedValue(sampleRestore)
    renderWithClient(wizardRoutes, wizardUrl)
    await reachConfirmStep()
    fireEvent.click(screen.getByRole('button', { name: /Geri yüklemeyi başlat/i }))
    await waitFor(() => expect(RestoreApi.startRestore).toHaveBeenCalledTimes(1))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(vi.mocked(RestoreApi.startRestore).mock.calls[0][2].confirmOverwrite).toBeUndefined()
    expect(vi.mocked(RestoreApi.startRestore).mock.calls[0][2].options).toEqual({ terminateActiveConnections: false, verifyChecksum: true, verifyBackup: true })
  })

  describe('when starting the restore fails', () => {
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
      vi.mocked(RestoreApi.startRestore).mockRejectedValueOnce(new TypeError('network down')).mockResolvedValueOnce(sampleRestore)
      renderWithClient(wizardRoutes, wizardUrl)
      await reachConfirmStep('replace')
      fireEvent.click(screen.getByRole('button', { name: /Geri yüklemeyi başlat/i }))
      const dialog = await screen.findByRole('dialog')
      fireEvent.change(within(dialog).getByRole('textbox'), { target: { value: 'SalesDB' } })
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
    listRestores({ ...sampleRestorePage, items: [fileRestore] })
    const list = renderWithClient(<RestoresPage />)
    expect(await screen.findByText('Dosya / klasör geri yükleme')).toBeInTheDocument()
    expect(screen.getByText('recovery')).toBeInTheDocument()
    list.unmount()
    vi.mocked(RestoreApi.getRestore).mockResolvedValue(fileRestore)
    renderWithClient(<Routes><Route path="/tenants/:tenantId/restores/:id" element={<RestoreDetailPage />} /></Routes>, '/tenants/tenant-a/restores/restore-1')
    await screen.findByRole('heading', { name: /Geri yükleme · RestoredSalesDB/ })
    fireEvent.click(screen.getByRole('tab', { name: 'Seçenekler' }))
    expect(screen.getByText('reports, notes.txt')).toBeInTheDocument()
    expect(screen.getByText('recovery')).toBeInTheDocument()
    expect(screen.queryByText('Etkin bağlantıları sonlandır')).not.toBeInTheDocument()
  })

  const fileWizard = <Routes><Route path="/tenants/:tenantId/backup-runs/:id/restore" element={<RestoreWizardPage />} /><Route path="/tenants/:tenantId/restores/:id" element={<p>File restore started</p>} /></Routes>
  const next = () => fireEvent.click(screen.getByRole('button', { name: /Devam et/ }))
  const field = (name: string) => screen.getByLabelText(name)
  /** Walks an eligible file wizard (one computer, so no Kaynak step) through Ne and Nereye up to the confirmation step. */
  async function reachFileConfirmStep(overwrite = false) {
    fireEvent.click(await screen.findByRole('button', { name: /Devam et/ }))
    if (overwrite) fireEvent.click(screen.getByRole('checkbox', { name: 'Mevcut dosyaların üzerine yazmaya izin ver' }))
    next()
    await screen.findByRole('button', { name: 'Dosyaları geri yükle' })
  }
  const twoComputers = () => vi.mocked(RestoreApi.getRestoreTargets).mockResolvedValue({ ...sampleTargets, engine: 'file-system', agents: [sampleTargets.agents[0], { ...sampleTargets.agents[0], agentId: 'agent-3', hostName: 'Second Agent' }] })

  it('has the three screens Ne, Nereye and Onay, skipping Kaynak when one computer fits, and runs the check on the way to Onay', async () => {
    renderWithClient(fileWizard, wizardUrl)
    await screen.findByRole('radio', { name: 'Tüm dosya ve klasörler' })
    const steps = screen.getByRole('listitem', { current: 'step' }).parentElement?.textContent ?? ''
    expect(steps).toMatch(/Ne.*Nereye.*Onay/)
    expect(steps).not.toMatch(/Kaynak/)
    expect(stepperCurrent()).toMatch(/Ne/)
    next()
    expect(stepperCurrent()).toMatch(/Nereye/)
    expect(RestoreApi.startRestorePrecheck).not.toHaveBeenCalled()
    next()
    await screen.findByRole('button', { name: 'Dosyaları geri yükle' })
    expect(stepperCurrent()).toMatch(/Onay/)
    expect(RestoreApi.startRestorePrecheck).toHaveBeenCalledTimes(1)
    expect(screen.getByText(/Tüm ön kontroller başarıyla tamamlandı/)).toBeInTheDocument()
  })

  it('asks for the computer on Kaynak first when more than one fits', async () => {
    twoComputers()
    renderWithClient(fileWizard, wizardUrl)
    await screen.findByRole('combobox', { name: 'Yedekleme bilgisayarı' })
    expect(stepperCurrent()).toMatch(/Kaynak/)
    next()
    expect(stepperCurrent()).toMatch(/Ne/)
  })

  it('blocks an ineligible computer from going past Kaynak', async () => {
    twoComputers()
    renderWithClient(fileWizard, wizardUrl)
    fireEvent.change(await screen.findByRole('combobox', { name: 'Yedekleme bilgisayarı' }), { target: { value: '' } })
    expect(screen.getByRole('button', { name: /Devam et/ })).toBeDisabled()
    expect(RestoreApi.startRestorePrecheck).not.toHaveBeenCalled()
  })

  it('does not go to Onay when the check fails, and shows why', async () => {
    const failed: RestoreView = { ...passed, id: 'failed-precheck', status: 'Failed', failureCode: 'StorageUnavailable' }
    vi.mocked(RestoreApi.startRestorePrecheck).mockResolvedValue(failed)
    vi.mocked(RestoreApi.getRestore).mockResolvedValue(failed)
    renderWithClient(fileWizard, wizardUrl)
    fireEvent.click(await screen.findByRole('button', { name: /Devam et/ }))
    next()
    await waitFor(() => expect(RestoreApi.startRestorePrecheck).toHaveBeenCalledTimes(1))
    expect(await screen.findByRole('alert')).toBeInTheDocument()
    expect(stepperCurrent()).toMatch(/Nereye/)
    expect(screen.queryByRole('button', { name: 'Dosyaları geri yükle' })).not.toBeInTheDocument()
  })

  it('shows no file source reference or target folder field and sends none from the user', async () => {
    renderWithClient(fileWizard, wizardUrl)
    fireEvent.click(await screen.findByRole('button', { name: /Devam et/ }))
    expect(screen.queryByLabelText(/Hedef dosya kaynağı referansı/)).not.toBeInTheDocument()
    expect(screen.queryByLabelText(/Hedef göreli klasör/)).not.toBeInTheDocument()
    next()
    await screen.findByRole('button', { name: 'Dosyaları geri yükle' })
    expect(screen.queryByText('Hedef dosya kaynağı referansı')).not.toBeInTheDocument()
    expect(RestoreApi.startRestorePrecheck).toHaveBeenCalledWith('tenant-a', 'artifact-1', expect.objectContaining({
      databaseName: 'SalesDB', mode: 'NewDatabase',
      options: { terminateActiveConnections: false, verifyChecksum: true, verifyBackup: true, files: { paths: null, relativeDirectory: 'restored' } },
    }), expect.any(String))
  })

  it('restores the whole directory to its original location, asking for the name only right before the overwrite starts', async () => {
    renderWithClient(fileWizard, wizardUrl)
    fireEvent.click(await screen.findByRole('button', { name: /Devam et/ }))
    fireEvent.click(await screen.findByRole('radio', { name: 'Özgün konum' }))
    fireEvent.click(screen.getByRole('checkbox', { name: 'Mevcut dosyaların üzerine yazmaya izin ver' }))
    // Nothing has to be typed to check the restore.
    next()
    await screen.findByRole('button', { name: 'Dosyaları geri yükle' })
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

  it('restores only selected paths under the alternate folder and discards a check that no longer matches the settings', async () => {
    renderWithClient(fileWizard, wizardUrl)
    fireEvent.click(await screen.findByRole('radio', { name: 'Seçili dosya ve klasörler' }))
    expect(screen.getByRole('button', { name: /Devam et/ })).toBeDisabled()
    fireEvent.change(field('Geri yüklenecek göreli yollar'), { target: { value: 'reports\nnotes.txt' } })
    next()
    next()
    await screen.findByRole('button', { name: 'Dosyaları geri yükle' })
    expect(RestoreApi.startRestorePrecheck).toHaveBeenCalledWith('tenant-a', 'artifact-1', expect.objectContaining({
      databaseName: 'SalesDB', mode: 'NewDatabase', options: expect.objectContaining({ files: { paths: ['reports', 'notes.txt'], relativeDirectory: 'restored' } }),
    }), expect.any(String))

    // Changing what is restored discards the check: Onay opens again only after a new one passed.
    fireEvent.click(screen.getByRole('button', { name: /^← Geri/ }))
    fireEvent.click(screen.getByRole('button', { name: /^← Geri/ }))
    fireEvent.change(field('Geri yüklenecek göreli yollar'), { target: { value: 'other.txt' } })
    next()
    next()
    await waitFor(() => expect(RestoreApi.startRestorePrecheck).toHaveBeenCalledTimes(2))
    fireEvent.click(await screen.findByRole('button', { name: 'Dosyaları geri yükle' }))
    await waitFor(() => expect(RestoreApi.startRestore).toHaveBeenCalledWith('tenant-a', 'artifact-1', expect.objectContaining({
      databaseName: 'SalesDB', precheckId: 'file-precheck', options: expect.objectContaining({ files: { paths: ['other.txt'], relativeDirectory: 'restored' } }),
    }), expect.any(String)))
  })

  it('keeps the same idempotency key when a precheck request has an uncertain outcome', async () => {
    vi.mocked(RestoreApi.startRestorePrecheck).mockRejectedValueOnce(new Error('timeout')).mockResolvedValue(passed)
    renderWithClient(fileWizard, wizardUrl)
    fireEvent.click(await screen.findByRole('button', { name: /Devam et/ }))
    next()
    const retry = await screen.findByRole('button', { name: /Tekrar Dene/ })
    await waitFor(() => expect(retry).toBeEnabled())
    fireEvent.click(retry)
    await waitFor(() => expect(RestoreApi.startRestorePrecheck).toHaveBeenCalledTimes(2))
    const calls = vi.mocked(RestoreApi.startRestorePrecheck).mock.calls
    expect(calls[1][3]).toBe(calls[0][3])
  })

  it('uses a new precheck key after the settings changed', async () => {
    vi.mocked(RestoreApi.startRestorePrecheck).mockRejectedValueOnce(new Error('timeout')).mockResolvedValue(passed)
    renderWithClient(fileWizard, wizardUrl)
    fireEvent.click(await screen.findByRole('button', { name: /Devam et/ }))
    next()
    await waitFor(() => expect(RestoreApi.startRestorePrecheck).toHaveBeenCalledTimes(1))
    await screen.findByRole('button', { name: /Tekrar Dene/ })
    fireEvent.click(screen.getByRole('button', { name: /^← Geri/ }))
    fireEvent.click(screen.getByRole('radio', { name: 'Seçili dosya ve klasörler' }))
    fireEvent.change(field('Geri yüklenecek göreli yollar'), { target: { value: 'notes.txt' } })
    next()
    next()
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
    await screen.findByRole('radio', { name: 'Tüm dosya ve klasörler' })
    expect(screen.getAllByRole('button', { name: 'Teknik ayrıntılar' }).length).toBeGreaterThan(0)
    expect(screen.queryByRole('button', { name: 'Kopyala' })).not.toBeInTheDocument()
    expect((await technicalWith('Yedek dosyası kimliği:')).getByRole('button', { name: 'Kopyala' })).toBeInTheDocument()
    await closeTechnical()
    await reachFileConfirmStep()
    expect(technicalRegions()).toHaveLength(0)
    expect(screen.queryByRole('button', { name: 'Kopyala' })).not.toBeInTheDocument()
    expect((await technicalWith('Yedekleme bilgisayarı kimliği:')).getByRole('button', { name: 'Kopyala' })).toBeInTheDocument()
  })

  it('disables original location when its source-agent mapping is unavailable', async () => {
    vi.mocked(RestoreApi.getRestoreTargets).mockResolvedValue({ ...sampleTargets, engine: 'file-system', targets: [] })
    renderWithClient(fileWizard, wizardUrl)
    fireEvent.click(await screen.findByRole('button', { name: /Devam et/ }))
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
  const visibleRawIds = () => [...document.body.querySelectorAll('*')].filter(node => node.children.length === 0 && rawId.test(node.textContent ?? '') && !node.closest('[role="region"][aria-label="Teknik ayrıntılar"]'))

  beforeEach(() => vi.mocked(RestoreApi.getRestoreTargets).mockResolvedValue({ ...sampleTargets, engine: 'file-system' }))

  async function showVerdict(view: RestoreView) {
    vi.mocked(RestoreApi.startRestorePrecheck).mockResolvedValue(view)
    vi.mocked(RestoreApi.getRestore).mockResolvedValue(view)
    renderWithClient(wizardRoutes, wizardUrl)
    fireEvent.click(await screen.findByRole('button', { name: /Devam et/ }))
    fireEvent.click(screen.getByRole('button', { name: /Devam et/ }))
  }

  it('names every check in plain words and keeps raw check ids in the collapsed technical details, copyable', async () => {
    await showVerdict(withChecks('Succeeded', [check('server.reachable', 'Passed', 'ok'), check('restore.target-sessions', 'Warning', 'sessions_open'), check('some.future-check', 'Passed', 'ok')]))
    await screen.findAllByRole('button', { name: 'Teknik ayrıntılar' })
    expect(visibleRawIds()).toEqual([])
    expect(screen.queryByText('some.future-check')).not.toBeInTheDocument()
    expect(screen.queryByText('restore.target-sessions')).not.toBeInTheDocument()
    expect(screen.getAllByRole('row').length).toBeGreaterThanOrEqual(4)
    const dialog = await technicalWith('restore.target-sessions')
    for (const id of ['server.reachable', 'restore.target-sessions', 'some.future-check']) expect(dialog.getByText(`${id}:`)).toBeInTheDocument()
    expect(dialog.getByRole('button', { name: 'Kopyala' })).toBeInTheDocument()
  })

  it('explains a failed check in plain words without exposing its reason code', async () => {
    await showVerdict(withChecks('Failed', [check('restore.disk-space', 'Failed', 'InsufficientDiskSpace')]))
    await screen.findAllByRole('button', { name: 'Teknik ayrıntılar' })
    expect(visibleRawIds()).toEqual([])
    expect(screen.queryAllByText(/InsufficientDiskSpace/)).toEqual([])
    expect((await technicalWith(/InsufficientDiskSpace/)).getAllByText(/InsufficientDiskSpace/).length).toBeGreaterThan(0)
    await closeTechnical()
    expect(screen.getAllByText('Hedefte yeterli disk alanı yok.').length).toBeGreaterThan(0)
    expect(screen.getByRole('button', { name: /Devam et/ })).toBeDisabled()
  })

  it('shows a repeated check once, however many times the agent reported it', async () => {
    await showVerdict(withChecks('Succeeded', [check('restore.target-sessions', 'Passed', 'ok'), check('server.reachable', 'Passed', 'ok'), check('restore.target-sessions', 'Passed', 'ok')]))
    await screen.findAllByRole('button', { name: 'Teknik ayrıntılar' })
    expect((await technicalWith('restore.target-sessions')).getAllByText('restore.target-sessions:')).toHaveLength(1)
    await closeTechnical()
  })

  it('keeps the worse status when one check is reported twice with different results', async () => {
    await showVerdict(withChecks('Failed', [check('restore.disk-space', 'Passed', 'ok'), check('restore.disk-space', 'Failed', 'InsufficientDiskSpace')]))
    await screen.findAllByRole('button', { name: 'Teknik ayrıntılar' })
    expect((await technicalWith('restore.disk-space')).getAllByText('restore.disk-space:')).toHaveLength(1)
    await closeTechnical()
    expect(screen.getAllByText('Hedefte yeterli disk alanı yok.').length).toBeGreaterThan(0)
    expect(screen.getByRole('button', { name: /Devam et/ })).toBeDisabled()
  })
})

it('rechecks a failed precheck only when retry is safe', () => {
  const recheck = vi.fn()
  const view = render(<PrecheckResults data={{ ...sampleRestore, failureCode: 'VerificationFailed' }} failed passed={false} onRecheck={recheck} recheckDisabled={false} />)
  fireEvent.click(within(screen.getByRole('alert')).getByRole('button', { name: 'Yeniden dene' }))
  expect(recheck).toHaveBeenCalledOnce()
  view.rerender(<PrecheckResults data={{ ...sampleRestore, failureCode: 'ServerCertificateUntrusted' }} failed passed={false} onRecheck={recheck} recheckDisabled={false} />)
  expect(within(screen.getByRole('alert')).queryByRole('button', { name: 'Yeniden dene' })).not.toBeInTheDocument()
})
