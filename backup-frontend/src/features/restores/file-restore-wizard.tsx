import { useEffect, useId, useRef, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { tx, useLanguage } from '@/lib/i18n'
import { isUncertainError, useIdempotencyKey } from '@/lib/idempotency-key'
import { Stepper } from '@/components/ui/stepper'
import { validFilePath, validFilePaths } from '@/features/backup-jobs/file-path-list'
import type { BackupArtifact } from '@/features/artifacts/types'
import { useRestore, useStartRestore, useStartRestorePrecheck } from './hooks'
import { FileRestoreConfirmStep } from './file-restore-confirm-step'
import { FileRestoreScopeStep } from './file-restore-scope-step'
import { FileRestoreTargetStep } from './file-restore-target-step'
import { RestoreCheckStep } from './restore-check-step'
import { RestoreSourceSummary } from './restore-source-summary'
import type { RestoreTargetsResponse, RestoreView } from './types'

const localTarget = { host: 'localhost', port: null, integratedSecurity: true, databaseCredentialReference: null, encrypt: true, trustServerCertificate: false }

/** A failure belongs to the settings it happened under: changing them makes it irrelevant. */
interface Failure { fingerprint: string; error: unknown }

export function FileRestoreWizard({ tenantId, artifact, targets }: { tenantId: string; artifact: BackupArtifact; targets: RestoreTargetsResponse }) {
  useLanguage()
  const id = useId()
  const navigate = useNavigate()
  const originalTarget = targets.targets.find(target => target.jobId === artifact.jobId && target.database === targets.sourceDatabase)
  const originalAgent = targets.agents.find(agent => agent.agentId === originalTarget?.agentId && agent.canRestore && agent.canPrecheck && agent.sourceEligible)
  const [step, setStep] = useState(1)
  const [agentId, setAgentId] = useState(() => targets.agents.find(agent => agent.canRestore && agent.canPrecheck && agent.sourceEligible)?.agentId ?? '')
  const [location, setLocation] = useState<'original' | 'alternate'>('alternate')
  const [reference, setReference] = useState(targets.sourceDatabase)
  const [directory, setDirectory] = useState('restored')
  const [scope, setScope] = useState<'all' | 'selected'>('all')
  const [paths, setPaths] = useState<string[]>([])
  const [overwrite, setOverwrite] = useState(false)
  const [precheck, setPrecheck] = useState<{ fingerprint: string; data: RestoreView } | null>(null)
  const [precheckFailure, setPrecheckFailure] = useState<Failure | null>(null)
  const [executionFailure, setExecutionFailure] = useState<Failure | null>(null)
  const precheckKey = useIdempotencyKey()
  const executionKey = useIdempotencyKey()
  const pending = useRef(false)
  const mounted = useRef(true)
  const startPrecheck = useStartRestorePrecheck()
  const startRestore = useStartRestore()
  useEffect(() => { mounted.current = true; return () => { mounted.current = false } }, [])

  const original = location === 'original'
  const targetReference = original ? targets.sourceDatabase : reference
  const relativeDirectory = original ? '' : directory
  const request = {
    agentId, target: localTarget, databaseName: targetReference,
    mode: overwrite ? 'ReplaceExistingDatabase' as const : 'NewDatabase' as const,
    options: { terminateActiveConnections: false, verifyChecksum: true, verifyBackup: true, files: { paths: scope === 'all' ? null : paths, relativeDirectory } },
  }
  const fingerprint = JSON.stringify(request)
  const current = precheck?.fingerprint === fingerprint
  const executionFingerprint = JSON.stringify([fingerprint, precheck?.data.id])
  const precheckError = precheckFailure?.fingerprint === fingerprint ? precheckFailure.error : null
  const executionError = executionFailure?.fingerprint === executionFingerprint ? executionFailure.error : null
  const query = useRestore(current && precheck ? precheck.data.id : '')
  const result = current && precheck ? query.data ?? precheck.data : undefined
  const failed = result?.status === 'Failed' || result?.result?.preCheck?.some(item => item.status === 'Failed') === true
  const passed = result?.purpose === 'Precheck' && result.status === 'Succeeded' && !failed
  const agent = targets.agents.find(item => item.agentId === agentId)
  const referenceValid = /^[A-Za-z0-9_.-]{1,128}$/.test(targetReference)
  const eligible = Boolean(artifact.state === 'Available' && agent?.canRestore && agent.canPrecheck && agent.sourceEligible && (!original || agent.agentId === originalAgent?.agentId))
  const directoryValid = original || directory === '' || validFilePath(directory)
  const pathsValid = scope === 'all' || validFilePaths(paths)
  const valid = eligible && referenceValid && directoryValid && pathsValid
  // Only a passed check that matches the current settings opens the confirmation step.
  const shownStep = step === 4 && !passed ? 3 : step

  async function check(fresh = false) {
    if (!valid || pending.current) return
    if (fresh) precheckKey.reset()
    const attempt = { fingerprint, variables: { artifactId: artifact.id, request, idempotencyKey: precheckKey.keyFor(request) } }
    executionKey.reset()
    setPrecheck(null)
    setPrecheckFailure(null)
    setExecutionFailure(null)
    pending.current = true
    try {
      const data = await startPrecheck.mutateAsync(attempt.variables)
      precheckKey.settle()
      if (!mounted.current) return
      setPrecheck({ fingerprint: attempt.fingerprint, data })
      setStep(3)
    } catch (error) {
      precheckKey.settle(error)
      if (mounted.current) setPrecheckFailure({ fingerprint: attempt.fingerprint, error })
    } finally { pending.current = false }
  }

  async function restore() {
    if (!valid || !passed || !current || !precheck || pending.current) return
    const executionRequest = { ...request, precheckId: precheck.data.id, ...(overwrite ? { confirmOverwrite: targetReference } : {}) }
    const attempt = { fingerprint: executionFingerprint, variables: { artifactId: artifact.id, idempotencyKey: executionKey.keyFor(executionRequest), request: executionRequest } }
    setExecutionFailure(null)
    pending.current = true
    try {
      const data = await startRestore.mutateAsync(attempt.variables)
      executionKey.settle()
      if (mounted.current) navigate(`/tenants/${tenantId}/restores/${data.id}`)
    } catch (error) {
      executionKey.settle(error)
      if (mounted.current) setExecutionFailure({ fingerprint: attempt.fingerprint, error })
    } finally { pending.current = false }
  }

  return <div className="space-y-6">
    <Stepper steps={[tx('Nereye'), tx('Nasıl'), tx('Kontrol'), tx('Onay')]} current={shownStep - 1} />
    <RestoreSourceSummary artifact={artifact} isPg={false} fileArchive />

    {shownStep === 1 && <FileRestoreTargetStep
      instancePrefix={id} targets={targets} agent={agent} agentId={agentId} originalAgent={originalAgent} original={original}
      reference={reference} directory={directory} referenceValid={referenceValid} directoryValid={directoryValid} eligible={eligible}
      onAgentChange={setAgentId}
      onLocationChange={(next) => {
        setLocation(next)
        if (next === 'original' && originalAgent) setAgentId(originalAgent.agentId)
      }}
      onReferenceChange={setReference} onDirectoryChange={setDirectory} onContinue={() => setStep(2)}
    />}

    {shownStep === 2 && <FileRestoreScopeStep
      instancePrefix={id} scope={scope} paths={paths} pathsValid={pathsValid} overwrite={overwrite}
      precheckError={precheckError} precheckRetryable={isUncertainError(precheckError)} precheckStarting={startPrecheck.isPending} restoreStarting={startRestore.isPending}
      canCheck={valid} onScopeChange={setScope} onPathsChange={setPaths} onOverwriteChange={setOverwrite}
      onBack={() => setStep(1)} onRunPrecheck={() => void check()}
    />}

    {shownStep === 3 && <RestoreCheckStep
      fileArchive precheckData={result} precheckLoading={startPrecheck.isPending || (current && query.isPending)}
      precheckDisplayError={precheckError || (current && query.error)} precheckError={precheckError} precheckRetryable={isUncertainError(precheckError)}
      precheckFailed={failed} precheckPassed={passed} precheckStarting={startPrecheck.isPending} restoreStarting={startRestore.isPending}
      onRunPrecheck={(fresh) => void check(fresh)} onBack={() => setStep(2)} onContinue={() => setStep(4)}
    />}

    {shownStep === 4 && <FileRestoreConfirmStep
      artifact={artifact} agent={agent} agentId={agentId} original={original} targetReference={targetReference} relativeDirectory={relativeDirectory}
      scope={scope} paths={paths} overwrite={overwrite} precheckPassed={passed} precheckStarting={startPrecheck.isPending} restoreStarting={startRestore.isPending}
      executionError={executionError} executionRetryable={isUncertainError(executionError)} eligible={eligible}
      onBack={() => setStep(3)} onStart={() => void restore()}
    />}
  </div>
}
