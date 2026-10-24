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
import type { BackupArtifact } from '@/features/artifacts/types'
import type { RestoreCleanup, RestoreTargetsResponse, RestoreView } from './types'

const tenancy = vi.hoisted(() => ({ tenantId: 'tenant-a', canAdmin: true, canOperate: true }))
vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => tenancy }))
vi.mock('./api', () => ({
  getRestores: vi.fn(), getRestore: vi.fn(), getRestoreTargets: vi.fn(), startRestoreVerification: vi.fn(),
  retryRestoreCleanup: vi.fn(), removeRollbackCopy: vi.fn(), cancelRestore: vi.fn(),
}))
vi.mock('@/features/agents/api', () => ({ getAgents: vi.fn() }))
vi.mock('@/features/artifacts/api', () => ({ getArtifact: vi.fn() }))

const artifact = {
  id: 'artifact-1', backupType: 'Full', databaseName: 'SalesDB', state: 'Available', sizeBytes: 1,
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
const withResult = (restore: RestoreView, passed = true): RestoreView => ({ ...restore, result: { verification: {
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
  tenancy.canAdmin = true
  tenancy.canOperate = true
  vi.mocked(RestoreApi.getRestores).mockResolvedValue(page([]))
  vi.mocked(RestoreApi.getRestoreTargets).mockResolvedValue(targets)
  vi.mocked(RestoreApi.getRestore).mockResolvedValue(base)
  vi.mocked(AgentApi.getAgents).mockResolvedValue([])
  vi.mocked(ArtifactApi.getArtifact).mockResolvedValue(artifact)
})
afterEach(() => { cleanup(); vi.clearAllMocks() })

describe('artifact restore verification', () => {
  it('shows "not tested" and starts a verification without database name or mode', async () => {
    vi.mocked(RestoreApi.startRestoreVerification).mockResolvedValue(base)
    renderAt(<Routes><Route path="/" element={<ArtifactVerificationSection tenantId="tenant-a" artifact={artifact} />} /><Route path="/tenants/tenant-a/restores/:id" element={<p>detail page</p>} /></Routes>)
    expect(await screen.findByText('Henüz test edilmedi.')).toBeInTheDocument()
    expect(RestoreApi.getRestores).toHaveBeenCalledWith('tenant-a', { purpose: 'Verification', artifactId: 'artifact-1', pageSize: 10 }, expect.anything())
    fireEvent.click(screen.getByRole('button', { name: "Geri yükleme testi başlat" }))
    expect(await screen.findByText(/(?:Production Veritabanı değiştirilmez|Canlı veritabanı değiştirilmez)/)).toBeInTheDocument()
    const agentOptions = within(screen.getByLabelText("Ajan")).getAllByRole('option')
    expect(agentOptions).toHaveLength(1)
    expect(agentOptions[0]).toHaveTextContent('Verifier')
    fireEvent.click(screen.getByRole('button', { name: 'Testi başlat' }))
    await waitFor(() => expect(RestoreApi.startRestoreVerification).toHaveBeenCalled())
    const [tenant, artifactId, request, key] = vi.mocked(RestoreApi.startRestoreVerification).mock.calls[0]
    expect([tenant, artifactId]).toEqual(['tenant-a', 'artifact-1'])
    expect(request).toEqual({ agentId: 'agent-1', target: targets.targets[0].target, options: { verifyChecksum: true, verifyBackup: true } })
    expect(request).not.toHaveProperty('databaseName')
    expect(request).not.toHaveProperty('mode')
    expect(key).toMatch(/[0-9a-f-]{36}/)
    expect(await screen.findByText('detail page')).toBeInTheDocument()
  })

  it('hides the action from viewers and renders history with pending cleanup', async () => {
    tenancy.canOperate = false
    vi.mocked(RestoreApi.getRestores).mockResolvedValue(page([
      { ...base, verificationOutcome: 'Failed', status: 'Failed', cleanup: cleanupOf({ state: 'Pending', attempts: 2 }) },
      { ...base, id: 'ver-0', cleanup: cleanupOf({}) },
    ]))
    renderAt(<ArtifactVerificationSection tenantId="tenant-a" artifact={artifact} />)
    expect(await screen.findByRole('table')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: "Geri yükleme testi başlat" })).not.toBeInTheDocument()
    expect(screen.getAllByText("Başarısız").length).toBeGreaterThan(0)
    expect(screen.getByText("Başarılı")).toBeInTheDocument()
    expect(screen.getByText(/otomatik yeniden denenecek \(deneme 2\)/)).toBeInTheDocument()
    expect(screen.getAllByText(/Süre: 30 sn|30 sn/).length).toBeGreaterThan(0)
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

  it('renders a Failed outcome with the server reason', async () => {
    vi.mocked(RestoreApi.getRestore).mockResolvedValue(withResult({ ...base, status: 'Failed', verificationOutcome: 'Failed', failureCode: 'VerificationFailed', reason: 'The backup restored, but checks failed.', cleanup: cleanupOf({}) }, false))
    renderDetail()
    expect(await screen.findByText(/The backup restored, but checks failed\./)).toBeInTheDocument()
    expect(screen.getByText('Başarısız', { selector: 'li span.font-medium' })).toBeInTheDocument()
    expect(screen.queryByText(/Hedef veritabanını kontrol edin/)).not.toBeInTheDocument()
  })

  it('renders NotRun for a cancelled verification', async () => {
    vi.mocked(RestoreApi.getRestore).mockResolvedValue({ ...base, status: 'Cancelled', verificationOutcome: 'NotRun', cleanup: cleanupOf({}) })
    renderDetail()
    expect(await screen.findByText("Çalıştırılmadı · Çalıştırılmadı")).toBeInTheDocument()
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

  it('hides the removal action from non-administrators', async () => {
    tenancy.canAdmin = false
    vi.mocked(RestoreApi.getRestore).mockResolvedValue(retained)
    renderDetail()
    expect(await screen.findByText(/(?:Otomatik olarak asla kaldırılmaz|otomatik olarak kaldırılmaz)/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: "Geri alma kopyasını kaldır" })).not.toBeInTheDocument()
  })
})

describe('restores list filter', () => {
  it('switches to restore tests with purpose=Verification', async () => {
    vi.mocked(RestoreApi.getRestores).mockImplementation(async (_tenant, filters) => page(filters?.purpose === 'Verification' ? [{ ...base, sourceDatabaseName: 'SalesDB', cleanup: cleanupOf({}) }] : []))
    renderAt(<RestoresPage />)
    await waitFor(() => expect(RestoreApi.getRestores).toHaveBeenCalledWith('tenant-a', expect.objectContaining({ purpose: undefined }), expect.anything()))
    fireEvent.click(screen.getByRole('tab', { name: "Geri yükleme testleri" }))
    expect(await screen.findByText("Başarılı")).toBeInTheDocument()
    expect(RestoreApi.getRestores).toHaveBeenCalledWith('tenant-a', expect.objectContaining({ purpose: 'Verification' }), expect.anything())
    expect(screen.queryByText("Hedef veritabanı")).not.toBeInTheDocument()
  })
})
