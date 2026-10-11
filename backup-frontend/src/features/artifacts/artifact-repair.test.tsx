import { render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { BackupArtifact, ReplicaRepairOperation, StartReplicaRepairRequest } from './types'
import { ArtifactDetailPage } from '@/pages/artifacts/artifact-detail-page'

const state = vi.hoisted(() => ({
  artifact: undefined as BackupArtifact | undefined,
  repairOp: undefined as ReplicaRepairOperation | undefined,
  startRepairFn: vi.fn(),
  cancelRepairFn: vi.fn(),
}))

vi.mock('@/features/tenancy/use-tenant', () => ({
  useTenant: () => ({ tenantId: 'tenant-1', canAdmin: true, canOperate: true }),
}))

vi.mock('@/features/artifacts/hooks', () => ({
  useArtifact: () => ({ data: state.artifact, isPending: false, error: null, refetch: vi.fn() }),
  useArtifactActions: () => ({
    requestDelete: { isPending: false, error: null, mutate: vi.fn(), reset: vi.fn() },
    cancelDelete: { isPending: false, error: null, mutate: vi.fn(), reset: vi.fn() },
  }),
  useReplicaRepair: () => ({
    startRepair: {
      isPending: false,
      error: null,
      mutate: (req: StartReplicaRepairRequest, opts?: { onSuccess?: (op: ReplicaRepairOperation) => void }) => {
        state.startRepairFn(req)
        opts?.onSuccess?.(state.repairOp ?? { id: 'repair-1', artifactId: 'artifact-1', storageConnectionId: 'sftp-conn', storageProvider: 'sftp', sourceReplicaId: 'rep-1', targetReplicaId: null, status: 'InProgress', failureCode: null, failureReason: null, transferredBytes: 0, totalBytes: 1048576, createdAt: '2026-10-11T12:00:00Z', startedAt: null, completedAt: null, cancellationRequestedAt: null, requestedByUserId: null })
      },
    },
    cancelRepair: {
      isPending: false,
      error: null,
      mutate: (id: string, opts?: { onSuccess?: () => void }) => {
        state.cancelRepairFn(id)
        opts?.onSuccess?.()
      },
    },
  }),
  useRepairStatus: () => ({
    data: state.repairOp,
    isPending: false,
    error: null,
  }),
}))

const sampleArtifact = (destState = 'Failed', hasVerifiedReplica = true): BackupArtifact => ({
  id: 'artifact-1',
  tenantId: 'tenant-1',
  backupType: 'Full',
  sizeBytes: 1048576,
  sourceChecksum: 'a'.repeat(64),
  artifactChecksum: 'b'.repeat(64),
  checksumAlgorithm: 'sha256',
  createdAt: '2026-10-11T12:00:00Z',
  state: 'Available',
  acceptedAt: '2026-10-11T12:00:00Z',
  retentionExpiresAt: '2026-11-11T12:00:00Z',
  retentionPinnedAt: null,
  deleteAfter: null,
  deleteReason: null,
  cancelDeleteAllowed: false,
  jobName: 'Test Job',
  databaseName: 'TestDb',
  agentName: 'Test Agent',
  runId: 'run-1',
  jobId: 'job-1',
  attemptId: 'attempt-1',
  replicas: hasVerifiedReplica ? [
    {
      id: 'rep-1',
      storageProvider: 'central',
      storageConnectionId: 'central-conn',
      storageTarget: 'central',
      key: 'central/key',
      sizeBytes: 1048576,
      checksum: 'b'.repeat(64),
      checksumAlgorithm: 'sha256',
      verificationState: 'Verified',
      verificationMethod: 'ReadBack',
      createdAt: '2026-10-11T12:00:00Z',
      verifiedAt: '2026-10-11T12:00:00Z',
      status: 'Available',
      usable: true,
      deleteRequestedAt: null,
      deleteAfter: null,
      deleteReason: null,
      deletedAt: null,
      deleteAttempts: 0,
      deleteFailureCode: null,
      deleteNextAttemptAt: null,
      deletionExecutor: 'Backend',
      waitingForAgent: false,
    },
  ] : [],
  replication: {
    requiredVerifiedReplicas: 2,
    verifiedReplicas: 1,
    destinationCount: 2,
    status: 'Degraded',
    policySatisfied: false,
    destinations: [
      {
        storageProvider: 'central',
        storageConnectionId: 'central-conn',
        isPrimary: true,
        state: 'Verified',
        failureCode: null,
        failureReason: null,
        objectMayRemain: false,
        replicaId: 'rep-1',
        replicaStatus: 'Available',
      },
      {
        storageProvider: 'sftp',
        storageConnectionId: 'sftp-conn',
        isPrimary: false,
        state: destState,
        failureCode: destState === 'Failed' ? 'StorageUnavailable' : null,
        failureReason: destState === 'Failed' ? 'SFTP connection timed out.' : null,
        objectMayRemain: false,
        replicaId: null,
        replicaStatus: null,
      },
    ],
  },
})

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={['/tenants/tenant-1/artifacts/artifact-1']}>
        <Routes>
          <Route path="/tenants/:tenantId/artifacts/:id" element={<ArtifactDetailPage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>
  )
}

describe('Manual replica repair frontend flow', () => {
  beforeEach(() => {
    state.artifact = undefined
    state.repairOp = undefined
    state.startRepairFn.mockClear()
    state.cancelRepairFn.mockClear()
  })

  it('renders Repair button for failed destination when verified source exists', async () => {
    state.artifact = sampleArtifact('Failed', true)
    renderPage()

    const repairButton = screen.getByRole('button', { name: /onar/i })
    expect(repairButton).toBeInTheDocument()
  })

  it('does not render Repair button for verified destination', async () => {
    state.artifact = sampleArtifact('Verified', true)
    renderPage()

    expect(screen.queryByRole('button', { name: /^onar$/i })).not.toBeInTheDocument()
  })

  it('does not render Repair button when no verified source replica exists', async () => {
    state.artifact = sampleArtifact('Failed', false)
    renderPage()

    expect(screen.queryByRole('button', { name: /^onar$/i })).not.toBeInTheDocument()
  })

  it('clicking Repair triggers start repair and renders in-progress state', async () => {
    state.artifact = sampleArtifact('Failed', true)
    state.repairOp = {
      id: 'repair-1',
      artifactId: 'artifact-1',
      storageConnectionId: 'sftp-conn',
      storageProvider: 'sftp',
      sourceReplicaId: 'rep-1',
      targetReplicaId: null,
      status: 'InProgress',
      failureCode: null,
      failureReason: null,
      transferredBytes: 524288,
      totalBytes: 1048576,
      createdAt: '2026-10-11T12:00:00Z',
      startedAt: '2026-10-11T12:00:01Z',
      completedAt: null,
      cancellationRequestedAt: null,
      requestedByUserId: null,
    }

    renderPage()

    const user = userEvent.setup()
    const repairButton = screen.getByRole('button', { name: /^onar$/i })
    await user.click(repairButton)

    expect(state.startRepairFn).toHaveBeenCalledWith({
      storageConnectionId: 'sftp-conn',
      storageProvider: 'sftp',
    })

    expect(screen.getByText(/Onarım işlemi devam ediyor/i)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /Onarımı iptal et/i })).toBeInTheDocument()
  })
})
