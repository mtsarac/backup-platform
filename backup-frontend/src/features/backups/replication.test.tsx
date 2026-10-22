import { fireEvent, screen, within } from '@testing-library/react'
import { render } from '@/test/render'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { ReplicationHealth } from '@/features/artifacts/types'
import { makeRun } from '@/features/backups/test-data'
import type { BackupRun, BackupRunPage } from '@/features/backups/types'
import { BackupDetailPage } from '@/pages/backups/backup-detail-page'
import { BackupsPage } from '@/pages/backups/backups-page'
import { failureMessage } from '@/lib/api/failure-messages'
import { ReplicationBadge, ReplicationSection, ReplicationSummary } from './replication'

const state = vi.hoisted(() => ({
  run: undefined as BackupRun | undefined,
  page: undefined as BackupRunPage | undefined,
}))
vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => ({ tenantId: 'tenant-a', canOperate: true }) }))
vi.mock('@/features/backups/hooks', () => ({
  useBackup: () => ({ data: state.run, isPending: false, error: null }),
  useBackupActions: () => ({
    retry: { isPending: false, error: null, mutateAsync: vi.fn(), reset: vi.fn() },
    cancel: { isPending: false, error: null, mutateAsync: vi.fn(), reset: vi.fn() },
    pause: { isPending: false, error: null, mutateAsync: vi.fn(), reset: vi.fn() },
    resume: { isPending: false, error: null, mutateAsync: vi.fn(), reset: vi.fn() },
  }),
  useBackups: () => ({ data: state.page, isPending: false, isFetching: false, error: null }),
}))
vi.mock('@/features/backup-jobs/hooks', () => ({ useBackupJobs: () => ({ data: [], isPending: false, error: null }) }))
vi.mock('@/features/agents/hooks', () => ({ useAgents: () => ({ data: [], isPending: false, error: null }) }))
vi.mock('@/features/artifacts/artifact-details', () => ({ ArtifactDetails: () => <p>Yedek çıktısı ayrıntıları</p> }))

const destination = (storageProvider: string, state: string, failureCode: string | null = null, isPrimary = false) => ({
  storageProvider, storageConnectionId: `${storageProvider}-id`, isPrimary, state, failureCode,
  failureReason: failureCode ? 'Yedek depolamaya yazılamadı.' : null, objectMayRemain: false, replicaId: state === 'Verified' ? `${storageProvider}-replica` : null, replicaStatus: state === 'Verified' ? 'Available' : null,
})

const healthOf = (status: ReplicationHealth['status'], verified: number, required = 2, destinations = [
  destination('central', 'Verified', null, true), destination('sftp', 'Verified'), destination('google-drive', 'Failed', 'StorageUnavailable'),
]): ReplicationHealth => ({ requiredVerifiedReplicas: required, verifiedReplicas: verified, destinationCount: 3, status, policySatisfied: verified >= required, destinations })

function renderDetail() {
  return render(<MemoryRouter initialEntries={['/tenants/tenant-a/backup-runs/run-id']}><Routes><Route path="/tenants/:tenantId/backup-runs/:id" element={<BackupDetailPage />} /></Routes></MemoryRouter>)
}

beforeEach(() => {
  state.run = undefined
  state.page = undefined
})

describe('replication compliance rendering', () => {
  it('explains quarantined conflicts without promising deletion or exposing backend exception text', () => {
    const conflict = { ...destination('sftp', 'Failed', 'TargetConflict'), replicaStatus: 'Quarantined', objectMayRemain: true, failureReason: 'provider-secret-exception' }
    render(<ReplicationSection replication={healthOf('Degraded', 2, 2, [conflict])} />)
    expect(screen.getByText(/Sahipliği doğrulanmadan silinmez/)).toBeInTheDocument()
    expect(screen.queryByText(/saklama süreci bu dosyayı da kaldırır/)).not.toBeInTheDocument()
    expect(screen.queryByText(/provider-secret-exception/)).not.toBeInTheDocument()
  })

  it.each([
    ['Healthy', 3, 'Çoğaltma sağlıklı'],
    ['Degraded', 2, 'Kısmi çoğaltma · ilke karşılandı'],
    ['InsufficientReplicas', 1, 'Doğrulanmış kopya sayısı yetersiz'],
    ['Unavailable', 0, 'Doğrulanmış kopya yok'],
  ] as const)('shows the backend status %s and the verified/required counts', (status, verified, label) => {
    render(<ReplicationSummary replication={healthOf(status, verified)} />)
    expect(screen.getByText(label)).toBeInTheDocument()
    expect(screen.getByText(`${verified} / 2 doğrulanmış kopya gerekli · 3 hedef`)).toBeInTheDocument()
  })

  it('renders nothing for a legacy run or artifact without replication', () => {
    const { container } = render(<><ReplicationBadge replication={undefined} /><ReplicationSummary replication={null} /><ReplicationSection replication={null} /></>)
    expect(container).toBeEmptyDOMElement()
  })

  it('does not recompute the status from the counts', () => {
    render(<ReplicationBadge replication={{ ...healthOf('Degraded', 3), status: 'Degraded' }} />)
    expect(screen.getByText("Kısmi çoğaltma · ilke karşılandı")).toBeInTheDocument()
    expect(screen.queryByText("Çoğaltma sağlıklı")).not.toBeInTheDocument()
  })

  it('lists each destination, naming the failed one with its reason next to the verified ones', () => {
    render(<ReplicationSection replication={healthOf('Degraded', 2)} />)
    const list = within(screen.getByRole('list', { name: 'Hedef sonuçları' }))
    expect(list.getAllByRole('listitem')).toHaveLength(3)
    expect(list.getAllByText('Doğrulandı')).toHaveLength(2)
    expect(list.getByText('Başarısız')).toBeInTheDocument()
    expect(list.getByText(failureMessage('StorageUnavailable'), { exact: false })).toBeInTheDocument()
    expect(list.queryByText(/Yedek depolamaya yazılamadı\./)).not.toBeInTheDocument()
    expect(screen.getByRole('status')).toHaveTextContent('Yedek kullanılabilir ve ilke karşılanıyor')
  })


  it('flags an object that may remain on a failed destination', () => {
    render(<ReplicationSection replication={healthOf('Degraded', 2, 2, [{ ...destination('sftp', 'Failed', 'StorageUnavailable'), objectMayRemain: true }, destination('central', 'Verified', null, true), destination('local', 'Verified')])} />)
    expect(screen.getByText(/tamamlanmamış bir dosya kalmış olabilir/)).toBeInTheDocument()
  })

  it('a Succeeded run with a degraded replication never shows a bare success', () => {
    state.run = makeRun({ status: 'Succeeded', replication: healthOf('Degraded', 2) })
    renderDetail()
    expect(screen.getAllByText('Başarılı').length).toBeGreaterThan(0)
    expect(screen.getAllByText("Kısmi çoğaltma · ilke karşılandı").length).toBeGreaterThan(0)
    fireEvent.click(screen.getByRole('tab', { name: "Çoğaltma" }))
    expect(screen.getByRole('region', { name: "Çoğaltma" })).toBeInTheDocument()
  })

  it('the run list shows the replication verdict beside the run status', () => {
    state.page = { items: [makeRun({ status: 'Succeeded', replication: healthOf('InsufficientReplicas', 1) })], totalCount: 1, page: 1, pageSize: 25 }
    render(<MemoryRouter><BackupsPage /></MemoryRouter>)
    expect(screen.getAllByText('Başarılı').length).toBeGreaterThan(0)
    expect(screen.getByText("Doğrulanmış kopya sayısı yetersiz")).toBeInTheDocument()
  })

  it('a pre-existing run without the field renders exactly as before', () => {
    state.run = makeRun({ status: 'Succeeded' })
    renderDetail()
    expect(screen.queryByRole('region', { name: "Çoğaltma" })).not.toBeInTheDocument()
  })
})
