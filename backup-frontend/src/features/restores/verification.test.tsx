import { cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react'
import { render } from '@/test/render'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ArtifactVerificationSection } from './verification'
import { RestoresPage } from '@/pages/restores/restores-page'
import { RestoreDetailPage } from '@/pages/restores/restore-detail-page'
import * as RestoreApi from './api'
import * as AgentApi from '@/features/agents/api'
import * as ArtifactApi from '@/features/artifacts/api'
import { statusLabel } from '@/lib/status'
import type { BackupArtifact } from '@/features/artifacts/types'
import type { RestoreCleanup, RestoreTargetsResponse, RestoreView } from './types'

const tenancy = vi.hoisted(() => ({ tenantId: 'tenant-a' }))
vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => tenancy }))
vi.mock('./api', () => ({
  getRestores: vi.fn(), getRestore: vi.fn(), getRestoreTargets: vi.fn(), startRestoreVerification: vi.fn(),
  retryRestoreCleanup: vi.fn(), removeRollbackCopy: vi.fn(), cancelRestore: vi.fn(),
}))
vi.mock('@/features/agents/api', () => ({ getAgents: vi.fn() }))
vi.mock('@/features/artifacts/api', () => ({ getArtifact: vi.fn() }))

const artifact = {
  id: 'artifact-1', runId: 'run-1', backupType: 'Full', databaseName: 'SalesDB', state: 'Available', sizeBytes: 1,
  replicas: [{ id: 'r1', verificationState: 'Verified', status: 'Available', usable: true }],
} as unknown as BackupArtifact

const targets: RestoreTargetsResponse = {
  artifactId: 'artifact-1', sourceDatabase: 'SalesDB', backupType: 'Full', sizeBytes: 1,
  agents: [
    { agentId: 'agent-1', hostName: 'Verifier', status: 'Active', connectivity: 'online', canRestore: true, canPrecheck: true, canDiscover: true, canVerify: true, sourceEligible: true, sourceAccess: 'CentralStorage', rejectionCode: null, rejectionMessage: null },
    { agentId: 'agent-2', hostName: 'RestoreOnly', status: 'Active', connectivity: 'online', canRestore: true, canPrecheck: true, canDiscover: true, canVerify: false, sourceEligible: true, sourceAccess: 'CentralStorage', rejectionCode: null, rejectionMessage: null },
  ],
  targets: [{ jobId: 'job-1', jobName: 'Nightly', agentId: 'agent-1', database: 'SalesDB', target: { host: 'sql1', port: 1433, integratedSecurity: false, databaseCredentialReference: 'sql_smoke', encrypt: true, trustServerCertificate: false } }],
}

const base: RestoreView = {
  id: 'ver-1', artifactId: 'artifact-1', replicaId: 'r1', agentId: 'agent-1', databaseName: 'bp_verify_ab12', status: 'Succeeded',
  mode: 'NewDatabase', overwritePolicy: 'Forbidden', sourceAccess: 'CentralStorage', purpose: 'Verification',
  target: targets.targets[0].target, options: {}, createdAt: '2026-10-08T12:10:00Z', startedAt: '2026-10-08T12:10:05Z', completedAt: '2026-10-08T12:10:35Z',
  cancellationRequestedAt: null, failureCode: null, reason: null, targetState: 'NotModified', targetStateSummary: null,
  progress: { stage: 'Completed', percent: 100, at: null }, replicaPinned: false, result: null, attempts: [],
  verificationOutcome: 'Restored',
}
const cleanupOf = (patch: Partial<RestoreCleanup>): RestoreCleanup => ({ kind: 'VerificationDatabase', state: 'Completed', databaseName: 'bp_verify_ab12', attempts: 1, nextAttemptAt: null, lastFailureCode: null, reason: null, needsAttention: false, canRequest: false, retainedUntil: null, ...patch })
const withResult = (restore: RestoreView, passed = true): RestoreView => ({ ...restore, result: { preCheck: [], warnings: [], verification: {
  checks: [{ checkId: 'database-online', status: 'Passed', reasonCode: 'ok', message: 'Online.' }, { checkId: 'integrity', status: passed ? 'Passed' : 'Failed', reasonCode: 'x', message: 'Integrity message.' }],
  userTableCount: 4, readableTableCount: 4, cleanup: 'Dropped', cleanupReasonCode: null,
}, retainedDatabaseName: null } as unknown as RestoreView['result'] })

function renderAt(ui: React.ReactElement, route = '/') {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(<QueryClientProvider client={client}><MemoryRouter initialEntries={[route]}>{ui}</MemoryRouter></QueryClientProvider>)
}
const page = (items: RestoreView[]) => ({ items, totalCount: items.length, page: 1, pageSize: 10 })
function renderDetail() {
  return renderAt(<Routes><Route path="/restores/:id" element={<RestoreDetailPage />} /></Routes>, '/restores/ver-1')
}

beforeEach(() => {
  vi.mocked(RestoreApi.getRestores).mockResolvedValue(page([]))
  vi.mocked(RestoreApi.getRestoreTargets).mockResolvedValue(targets)
  vi.mocked(RestoreApi.getRestore).mockResolvedValue(base)
  vi.mocked(AgentApi.getAgents).mockResolvedValue([])
  vi.mocked(ArtifactApi.getArtifact).mockResolvedValue(artifact)
})
afterEach(() => { cleanup(); vi.clearAllMocks() })

describe('artifact restore verification', () => {
  const withJob = { ...artifact, jobId: 'job-1' } as BackupArtifact
  const renderSection = (art: BackupArtifact = withJob) => renderAt(<Routes><Route path="/" element={<ArtifactVerificationSection tenantId="tenant-a" artifact={art} />} /><Route path="/tenants/tenant-a/restores/:id" element={<p>detail page</p>} /></Routes>)

  it('shows "not tested" and starts a verification with one click on the backup\'s own server and computer', async () => {
    vi.mocked(RestoreApi.startRestoreVerification).mockResolvedValue(base)
    renderSection()
    expect(await screen.findByText('Henüz test edilmedi.')).toBeInTheDocument()
    expect(RestoreApi.getRestores).toHaveBeenCalledWith('tenant-a', { purpose: 'Verification', artifactId: 'artifact-1', pageSize: 10 }, expect.anything())
    for (const gone of [/Hedef/, /Özel Bağlantı/, /Bağlantı Noktası/, /Sertifika/, /Windows kimlik/, /Yedekleme bilgisayarı/]) expect(screen.queryByLabelText(gone)).not.toBeInTheDocument()
    fireEvent.click(await screen.findByRole('button', { name: "Geri yükleme testi başlat" }))
    await waitFor(() => expect(RestoreApi.startRestoreVerification).toHaveBeenCalled())
    const [tenant, artifactId, request, key] = vi.mocked(RestoreApi.startRestoreVerification).mock.calls[0]
    expect([tenant, artifactId]).toEqual(['tenant-a', 'artifact-1'])
    expect(request).toEqual({ agentId: 'agent-1', target: targets.targets[0].target, options: { verifyChecksum: true, verifyBackup: true } })
    expect(request).not.toHaveProperty('databaseName')
    expect(request).not.toHaveProperty('mode')
    expect(key).toMatch(/[0-9a-f-]{36}/)
    expect(await screen.findByText('detail page')).toBeInTheDocument()
  })

  it('asks only for the computer when more than one can run the test', async () => {
    vi.mocked(RestoreApi.getRestoreTargets).mockResolvedValue({ ...targets, agents: [...targets.agents, { ...targets.agents[0], agentId: 'agent-3', hostName: 'Second' }] })
    vi.mocked(RestoreApi.startRestoreVerification).mockResolvedValue(base)
    renderSection()
    const select = await screen.findByLabelText('Yedekleme bilgisayarı')
    expect(within(select).getAllByRole('option').map((option) => option.textContent)).toEqual([expect.stringContaining('Verifier'), expect.stringContaining('Second')])
    fireEvent.change(select, { target: { value: 'agent-3' } })
    fireEvent.click(screen.getByRole('button', { name: "Geri yükleme testi başlat" }))
    await waitFor(() => expect(RestoreApi.startRestoreVerification).toHaveBeenCalled())
    expect(vi.mocked(RestoreApi.startRestoreVerification).mock.calls[0][2]).toEqual({ agentId: 'agent-3', target: targets.targets[0].target, options: { verifyChecksum: true, verifyBackup: true } })
  })

  it('cannot start when the backup\'s server is not known', async () => {
    renderSection({ ...withJob, jobId: 'gone' } as BackupArtifact)
    expect(await screen.findByText(/Yedeğin alındığı sunucu bulunamadığı/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: "Geri yükleme testi başlat" })).not.toBeInTheDocument()
  })

  it('renders history with pending cleanup', async () => {
    vi.mocked(RestoreApi.getRestores).mockResolvedValue(page([
      { ...base, verificationOutcome: 'Failed', status: 'Failed', cleanup: cleanupOf({ state: 'Pending', attempts: 2 }) },
      { ...base, id: 'ver-0', cleanup: cleanupOf({}) },
    ]))
    renderAt(<ArtifactVerificationSection tenantId="tenant-a" artifact={artifact} />)
    expect(await screen.findByRole('table')).toBeInTheDocument()
    expect(screen.getAllByText("Başarısız").length).toBeGreaterThan(0)
    expect(screen.getByText("Başarılı")).toBeInTheDocument()
    expect(screen.getByText(/otomatik yeniden denenecek \(deneme 2\)/)).toBeInTheDocument()
    expect(screen.getAllByText(/Süre: 30 sn|30 sn/).length).toBeGreaterThan(0)
  })

  it('lists the latest test once, in the history, not again as a summary line', async () => {
    vi.mocked(RestoreApi.getRestores).mockResolvedValue(page([base]))
    renderAt(<ArtifactVerificationSection tenantId="tenant-a" artifact={artifact} />)
    await screen.findByRole('table')
    expect(screen.getAllByText('Başarılı')).toHaveLength(1)
  })
})

describe('restore verification detail', () => {
  it('renders a Restored outcome with checks and removed temporary database', async () => {
    vi.mocked(RestoreApi.getRestore).mockResolvedValue(withResult({ ...base, cleanup: cleanupOf({}) }))
    renderDetail()
    expect((await screen.findAllByText("Başarılı")).length).toBeGreaterThan(0)
    expect(screen.getByText(/Veritabanı çevrimiçi/)).toBeInTheDocument()
    expect(screen.getByText(/4 okunabilir \/ 4 kullanıcı tablosu/)).toBeInTheDocument()
    expect(screen.getByText(/Geçici veritabanı kaldırıldı/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Temizliği yeniden dene' })).not.toBeInTheDocument()
  })

  it('shows the run status once while no outcome is reported, and no outcome row', async () => {
    vi.mocked(RestoreApi.getRestore).mockResolvedValue({ ...base, status: 'Running', verificationOutcome: undefined, completedAt: null })
    renderDetail()
    await screen.findByRole('heading', { level: 1, name: /Doğrulama denemesi/ })
    expect(screen.getAllByText(statusLabel('general', 'Running'))).toHaveLength(1)
    expect(screen.queryByText('Sonuç')).not.toBeInTheDocument()
  })

  it('keeps the server reason in copyable technical details', async () => {
    vi.mocked(RestoreApi.getRestore).mockResolvedValue(withResult({ ...base, status: 'Failed', verificationOutcome: 'Failed', failureCode: 'VerificationFailed', reason: 'The backup restored, but checks failed.', cleanup: cleanupOf({}) }, false))
    renderDetail()
    await screen.findByRole('heading', { level: 1, name: /Doğrulama denemesi/ })
    expect(screen.queryByText('The backup restored, but checks failed.')).not.toBeInTheDocument()
    fireEvent.click(screen.getAllByRole('button', { name: 'Teknik ayrıntılar' })[0])
    expect(await screen.findByText('The backup restored, but checks failed.')).toBeInTheDocument()
    expect(screen.getByText('Başarısız', { selector: 'li span.font-medium' })).toBeInTheDocument()
    expect(screen.queryByText(/Hedef veritabanını kontrol edin/)).not.toBeInTheDocument()
  })

  it.each([
    ['DatabaseUnavailable', 'Bilgisayarı kontrol et', '/tenants/tenant-a/agents/agent-1'],
    ['quota_exceeded', 'Depolamayı yönet', '/tenants/tenant-a/storage'],
    ['AuthenticationFailed', 'Bilgileri düzelt', '/tenants/tenant-a/backup-runs/run-1/restore'],
  ])('links the %s failure to its relevant screen', async (failureCode, label, href) => {
    vi.mocked(RestoreApi.getRestore).mockResolvedValue({ ...base, purpose: 'Execution', status: 'Failed', failureCode })
    renderDetail()
    expect(await screen.findByRole('link', { name: label })).toHaveAttribute('href', href)
  })

  it('does not offer operation links for an untrusted certificate', async () => {
    vi.mocked(RestoreApi.getRestore).mockResolvedValue({ ...base, purpose: 'Execution', status: 'Failed', failureCode: 'ServerCertificateUntrusted' })
    renderDetail()
    await screen.findByRole('heading', { level: 1, name: /Geri yükleme/ })
    expect(screen.queryByRole('link', { name: /Yeniden dene|Bilgileri düzelt/ })).not.toBeInTheDocument()
  })

  it('offers to run the restore test again with the certificate trusted, for that test only, and only for the certificate failure', async () => {
    const untrusted = { ...base, status: 'Failed' as const, verificationOutcome: 'Failed' as const, failureCode: 'ServerCertificateUntrusted', cleanup: cleanupOf({}) }
    vi.mocked(RestoreApi.getRestore).mockResolvedValue(untrusted)
    vi.mocked(RestoreApi.startRestoreVerification).mockResolvedValue({ ...base, id: 'ver-2' })
    renderDetail()
    const summary = await screen.findByText('Bu sunucunun sertifikasına yine de güven')
    expect(summary.closest('details')).not.toHaveAttribute('open')
    fireEvent.click(summary)
    expect(screen.getByText(/yalnızca bu geri yükleme testi için geçerlidir/)).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Güven ve yeniden dene' }))
    await waitFor(() => expect(RestoreApi.startRestoreVerification).toHaveBeenCalledWith('tenant-a', 'artifact-1',
      { agentId: 'agent-1', target: { ...base.target, trustServerCertificate: true }, options: { verifyChecksum: true, verifyBackup: true } }, expect.any(String)))
  })

  it.each(['DatabaseUnavailable', 'AuthenticationFailed', 'VerificationFailed'])('does not offer the certificate step for the general %s failure', async (failureCode) => {
    vi.mocked(RestoreApi.getRestore).mockResolvedValue({ ...base, status: 'Failed', verificationOutcome: 'Failed', failureCode, cleanup: cleanupOf({}) })
    renderDetail()
    await screen.findByRole('heading', { level: 1, name: /Doğrulama denemesi/ })
    expect(screen.queryByText('Bu sunucunun sertifikasına yine de güven')).not.toBeInTheDocument()
  })

  it('renders NotRun for a cancelled verification', async () => {
    vi.mocked(RestoreApi.getRestore).mockResolvedValue({ ...base, status: 'Cancelled', verificationOutcome: 'NotRun', cleanup: cleanupOf({}) })
    renderDetail()
    expect(await screen.findByText("Çalıştırılmadı")).toBeInTheDocument()
  })

  it('retries a pending cleanup and flags needsAttention', async () => {
    vi.mocked(RestoreApi.getRestore).mockResolvedValue({ ...base, cleanup: cleanupOf({ state: 'Pending', attempts: 3, canRequest: true, needsAttention: true }) })
    const retried = { ...base, cleanup: cleanupOf({ state: 'Pending', attempts: 4 }) }
    vi.mocked(RestoreApi.retryRestoreCleanup).mockImplementation(async () => { vi.mocked(RestoreApi.getRestore).mockResolvedValue(retried); return retried })
    renderDetail()
    expect(await screen.findByText(/Kaldırma dikkat gerektiriyor/)).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Temizliği yeniden dene' }))
    await waitFor(() => expect(RestoreApi.retryRestoreCleanup).toHaveBeenCalledWith('tenant-a', 'ver-1'))
    expect(await screen.findByText(/otomatik yeniden denenecek \(deneme 4\)/)).toBeInTheDocument()
  })
})

describe('rollback copy removal', () => {
  const retained: RestoreView = { ...base, purpose: 'Execution', verificationOutcome: null, databaseName: 'SalesDB', mode: 'ReplaceExistingDatabase',
    cleanup: cleanupOf({ kind: 'RollbackCopy', state: 'Retained', databaseName: 'bp_old_SalesDB_1', canRequest: true, retainedUntil: '2026-11-01T00:00:00Z' }) }

  it('requires typing the exact database name and posts it', async () => {
    vi.mocked(RestoreApi.getRestore).mockResolvedValue(retained)
    vi.mocked(RestoreApi.removeRollbackCopy).mockResolvedValue({ ...retained, cleanup: cleanupOf({ kind: 'RollbackCopy', state: 'Pending', databaseName: 'bp_old_SalesDB_1' }) })
    renderDetail()
    expect(await screen.findByText(/(?:Otomatik olarak asla kaldırılmaz|otomatik olarak kaldırılmaz)/)).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: "Geri alma kopyasını kaldır" }))
    const confirm = await screen.findByRole('button', { name: 'Kalıcı olarak kaldır' })
    expect(confirm).toBeDisabled()
    fireEvent.change(screen.getByRole("textbox", { name: /bp_old_SalesDB_1/ }), { target: { value: 'bp_old_SalesDB' } })
    expect(confirm).toBeDisabled()
    fireEvent.change(screen.getByRole("textbox", { name: /bp_old_SalesDB_1/ }), { target: { value: 'bp_old_SalesDB_1' } })
    fireEvent.click(confirm)
    await waitFor(() => expect(RestoreApi.removeRollbackCopy).toHaveBeenCalledWith('tenant-a', 'ver-1', 'bp_old_SalesDB_1'))
  })
})

describe('restores list', () => {
  it('shows one list with trial restores badged "Deneme" and no list-type selector', async () => {
    vi.mocked(RestoreApi.getRestores).mockResolvedValue(page([
      { ...base, id: 'trial-1', purpose: 'Verification', databaseName: 'bp_verify_1', sourceDatabaseName: 'SalesDB', cleanup: cleanupOf({}) },
      { ...base, id: 'real-1', purpose: 'Execution', databaseName: 'RestoredDb' },
    ]))
    renderAt(<RestoresPage />)
    expect(await screen.findByRole('link', { name: 'RestoredDb' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Doğrulama denemesi: SalesDB' })).toBeInTheDocument()
    expect(screen.queryByText('bp_verify_1')).not.toBeInTheDocument()
    expect(screen.getAllByText('Deneme')).toHaveLength(1)
    expect(screen.queryByRole('combobox', { name: 'Liste türü' })).not.toBeInTheDocument()
  })
})
