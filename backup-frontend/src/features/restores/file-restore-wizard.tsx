import { useEffect, useId, useRef, useState } from 'react'
import { Loader2 } from 'lucide-react'
import { restorePath } from '@/lib/routes'
import { useNavigate } from 'react-router-dom'
import { tx, useLanguage } from '@/lib/i18n'
import { isUncertainError, useIdempotencyKey } from '@/lib/idempotency-key'
import { Stepper } from '@/components/ui/stepper'
import { validFilePaths } from '@/features/backup-jobs/file-path-validation'
import type { BackupArtifact } from '@/features/artifacts/types'
import { useRestore, useStartRestore, useStartRestorePrecheck } from './hooks'
import { FileRestoreConfirmStep } from './file-restore-confirm-step'
import { FileRestoreSourceStep } from './file-restore-source-step'
import { FileRestoreTargetStep } from './file-restore-target-step'
import { FileRestoreWhatStep } from './file-restore-what-step'
import { PrecheckResults } from './precheck-results'
import { Section } from '@/components/ui/section'
import { RestoreSourceSummary } from './restore-source-summary'
import type { RestoreTargetsResponse, RestoreView } from './types'

/** Not user input: an alternate restore always goes into this folder under the configured root, so existing files stay untouched. */
const alternateDirectory = 'restored'

const localTarget = { host: 'localhost', port: null, integratedSecurity: true, databaseCredentialReference: null, encrypt: true, trustServerCertificate: false }

/** A failure belongs to the settings it happened under: changing them makes it irrelevant. */
interface Failure { fingerprint: string; error: unknown }

export function FileRestoreWizard({ tenantId, artifact, targets }: { tenantId: string; artifact: BackupArtifact; targets: RestoreTargetsResponse }) {
  useLanguage()
  const id = useId()
  const navigate = useNavigate()
  const originalTarget = targets.targets.find(target => target.jobId === artifact.jobId && target.database === targets.sourceDatabase)
  const originalAgent = targets.agents.find(agent => agent.agentId === originalTarget?.agentId && agent.canRestore && agent.canPrecheck && agent.sourceEligible)
  const eligibleAgents = targets.agents.filter(agent => agent.canRestore && agent.canPrecheck && agent.sourceEligible)
  const skipSource = eligibleAgents.length === 1
  const [step, setStep] = useState<'source' | 'what' | 'where'>(skipSource ? 'what' : 'source')
  // The settings the user asked to confirm; a later change makes it stale, which sends them back to Nereye.
  const [confirmFor, setConfirmFor] = useState<string | null>(null)
  const [agentId, setAgentId] = useState(() => eligibleAgents[0]?.agentId ?? '')
  const [location, setLocation] = useState<'original' | 'alternate'>('alternate')
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
  const targetReference = targets.sourceDatabase
  const relativeDirectory = original ? '' : alternateDirectory
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
  const eligible = Boolean(artifact.state === 'Available' && agent?.canRestore && agent.canPrecheck && agent.sourceEligible && (!original || agent.agentId === originalAgent?.agentId))
  const pathsValid = scope === 'all' || validFilePaths(paths)
  const valid = eligible && pathsValid
  const confirming = confirmFor === fingerprint

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
      if (mounted.current) navigate(restorePath(tenantId, data.id))
    } catch (error) {
      executionKey.settle(error)
      if (mounted.current) setExecutionFailure({ fingerprint: attempt.fingerprint, error })
    } finally { pending.current = false }
  }

  // Only a passed check that matches the current settings opens the confirmation step.
  const shownStep = confirming && passed ? 'confirm' : step
  const checking = confirming && !passed && precheckError == null
  const checkPanel = checking && (failed && result
    ? <Section title={tx("Ön kontrol")}><PrecheckResults data={result} failed passed={false} fileArchive onRecheck={() => void check(true)} recheckDisabled={startPrecheck.isPending || startRestore.isPending} /></Section>
    : <p role="status" className="flex items-center gap-2 text-sm text-text-muted"><Loader2 aria-hidden="true" className="size-4 animate-spin" />{tx("Ön kontrol çalışıyor…")}</p>)
  const labels = skipSource ? [tx('Ne'), tx('Nereye'), tx('Onay')] : [tx('Kaynak'), tx('Ne'), tx('Nereye'), tx('Onay')]
  const currentStep = (shownStep === 'source' ? 0 : shownStep === 'what' ? 1 : shownStep === 'where' ? 2 : 3) - (skipSource ? 1 : 0)

  function continueToConfirm() {
    setConfirmFor(fingerprint)
    if (!(current && passed)) void check()
  }


  return <div className="space-y-6">
    <Stepper steps={labels} current={currentStep} />
    <RestoreSourceSummary artifact={artifact} isPg={false} fileArchive />

    {shownStep === 'source' && <FileRestoreSourceStep
      instancePrefix={id} targets={targets} agent={agent} agentId={agentId} originalAgent={originalAgent} original={original}
      eligible={eligible} onAgentChange={setAgentId} onContinue={() => setStep('what')}
    />}

    {shownStep === 'what' && <FileRestoreWhatStep
      instancePrefix={id} scope={scope} paths={paths} pathsValid={pathsValid}
      onScopeChange={setScope} onPathsChange={setPaths} onBack={skipSource ? undefined : () => setStep('source')} onContinue={() => setStep('where')}
    />}

    {shownStep === 'where' && <FileRestoreTargetStep
      instancePrefix={id} originalAgent={originalAgent} original={original} overwrite={overwrite}
      precheckError={precheckError} precheckRetryable={isUncertainError(precheckError)} precheckStarting={startPrecheck.isPending} restoreStarting={startRestore.isPending}
      canCheck={valid && !checking} checkPanel={checkPanel}
      onLocationChange={(next) => {
        setLocation(next)
        if (next === 'original' && originalAgent) setAgentId(originalAgent.agentId)
      }}
      onOverwriteChange={setOverwrite}
      onBack={() => setStep('what')} onContinue={continueToConfirm}
    />}

    {shownStep === 'confirm' && <FileRestoreConfirmStep
      agent={agent} agentId={agentId} original={original} name={targetReference}
      scope={scope} paths={paths} overwrite={overwrite} precheckData={result} precheckPassed={passed} precheckStarting={startPrecheck.isPending} restoreStarting={startRestore.isPending}
      executionError={executionError} executionRetryable={isUncertainError(executionError)} eligible={eligible}
      onRecheck={() => void check(true)} onBack={() => setConfirmFor(null)} onStart={() => void restore()}
    />}
  </div>
}
