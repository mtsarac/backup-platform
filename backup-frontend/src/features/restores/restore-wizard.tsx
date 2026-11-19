import { tx, useLanguage } from '@/lib/i18n'
import { restorePath } from '@/lib/routes'
import { Loader2 } from 'lucide-react'
import { useEffect, useId, useMemo, useRef, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { Stepper } from '@/components/ui/stepper'
import { Section } from '@/components/ui/section'
import type { BackupArtifact } from '@/features/artifacts/types'
import { useRestore, useStartRestore, useStartRestoreDiscovery, useStartRestorePrecheck } from './hooks'
import type { RestoreMode, RestoreOptions, RestoreTarget, RestoreTargetsResponse, RestoreView, StartRestoreDiscoveryVariables, StartRestorePrecheckVariables, StartRestoreVariables } from './types'
import { isUncertainError, useIdempotencyKey } from '@/lib/idempotency-key'
import { RestoreSourceSummary } from './restore-source-summary'
import { RestoreSourceStep } from './restore-source-step'
import { RestoreTargetStep } from './restore-target-step'
import { RestoreModeStep } from './restore-mode-step'
import { PrecheckResults } from './precheck-results'
import { isActiveRestore } from './presentation'
import { RestoreConfirmStep } from './restore-confirm-step'

export interface RestoreWizardProps {
  tenantId: string
  artifact: BackupArtifact
  targets: RestoreTargetsResponse
}

interface RequestAttempt<Variables> {
  readonly fingerprint: string
  readonly variables: Variables
}

export function RestoreWizard({ tenantId, artifact, targets }: RestoreWizardProps) {
  useLanguage()
  const navigate = useNavigate()
  const instancePrefix = useId()
  const isPg = targets.engine === 'postgres'
  const engineName = isPg ? 'PostgreSQL' : 'SQL Server'
  const eligibleAgents = targets.agents.filter((a) => a.canRestore && a.sourceEligible)
  const skipSource = eligibleAgents.length === 1
  const defaultAgent = eligibleAgents[0] ?? targets.agents[0]
  const ownTarget = targets.targets.find((t) => t.jobId === artifact.jobId)?.target

  const [step, setStep] = useState<'source' | 'target'>(skipSource ? 'target' : 'source')
  const [confirming, setConfirming] = useState(false)
  const [loginPending, setLoginPending] = useState(false)
  const [selectedAgentId, setSelectedAgentId] = useState<string>(defaultAgent?.agentId ?? '')
  const [targetType, setTargetType] = useState<'own' | 'other'>(ownTarget ? 'own' : 'other')
  // The job's fixed connection values; only the address and, when needed, the login are asked.
  const [otherTarget, setOtherTarget] = useState<RestoreTarget>({
    host: '',
    port: isPg ? 5432 : null,
    integratedSecurity: !isPg,
    databaseCredentialReference: null,
    encrypt: true,
    trustServerCertificate: false,
  })

  // Set from the error panel when the server's certificate cannot be verified; it holds for this restore only and drops as soon as the computer or server changes.
  const [trustCertificate, setTrustCertificate] = useState(false)

  const [mode, setMode] = useState<RestoreMode>('NewDatabase')
  const newDatabaseName = (() => {
    const name = artifact.databaseName ? `${artifact.databaseName}_restored` : 'RestoredDB'
    return isPg && new TextEncoder().encode(name).length > 63 ? 'restored_db' : name
  })()
  const [targetDatabaseName, setTargetDatabaseName] = useState<string>(newDatabaseName)
  const options = useMemo<RestoreOptions>(() => ({
    terminateActiveConnections: mode === 'ReplaceExistingDatabase',
    verifyChecksum: true,
    verifyBackup: true,
  }), [mode])

  const [discoveryOperation, setDiscoveryOperation] = useState<{ fingerprint: string; id: string } | null>(null)
  const discoveryOperationId = discoveryOperation?.id ?? ''
  const [discoveryError, setDiscoveryError] = useState<unknown>(null)
  const discoveryQuery = useRestore(discoveryOperationId)
  const startDiscovery = useStartRestoreDiscovery()
  const discoveryAttemptRef = useRef<RequestAttempt<StartRestoreDiscoveryVariables> | null>(null)

  const [precheckOperation, setPrecheckOperation] = useState<{ fingerprint: string; id: string } | null>(null)
  const precheckOperationId = precheckOperation?.id ?? ''
  const [precheckDataState, setPrecheckDataState] = useState<RestoreView | null>(null)
  const [precheckError, setPrecheckError] = useState<unknown>(null)
  const precheckQuery = useRestore(precheckOperationId)
  const startPrecheck = useStartRestorePrecheck()
  const precheckAttemptRef = useRef<RequestAttempt<StartRestorePrecheckVariables> | null>(null)

  const startRestore = useStartRestore()
  const restoreAttemptRef = useRef<RequestAttempt<StartRestoreVariables> | null>(null)
  const discoveryKey = useIdempotencyKey()
  const precheckKey = useIdempotencyKey()
  const restoreKey = useIdempotencyKey()
  const pendingRef = useRef({ discovery: false, precheck: false, execution: false })
  const [executionError, setExecutionError] = useState<unknown>(null)

  useEffect(() => () => {
    discoveryAttemptRef.current = null
    precheckAttemptRef.current = null
    restoreAttemptRef.current = null
  }, [])

  const activeAgent = targets.agents.find((a) => a.agentId === selectedAgentId)
  const baseTarget = targetType === 'own' && ownTarget ? ownTarget : otherTarget
  const targetWithTrust = (trust: boolean): RestoreTarget => ({ ...baseTarget, trustServerCertificate: baseTarget.trustServerCertificate || trust })
  const effectiveTarget = targetWithTrust(trustCertificate)

  const eligibleForRestore = artifact.state === 'Available' && activeAgent?.canRestore && activeAgent?.sourceEligible
  const targetValid = Boolean(effectiveTarget.host) && (!isPg || (!effectiveTarget.integratedSecurity && Boolean(effectiveTarget.databaseCredentialReference)))
  const databaseNameValid = Boolean(targetDatabaseName.trim()) && (!isPg || new TextEncoder().encode(targetDatabaseName.trim()).length <= 63)

  const precheckFingerprintFor = (target: RestoreTarget) => JSON.stringify([JSON.stringify([selectedAgentId, target]), targetDatabaseName.trim(), mode, options])
  const discoveryFingerprint = JSON.stringify([selectedAgentId, effectiveTarget])
  const precheckFingerprint = precheckFingerprintFor(effectiveTarget)
  const currentDiscovery = discoveryOperation?.fingerprint === discoveryFingerprint
  const currentPrecheck = precheckOperation?.fingerprint === precheckFingerprint
  const discoveryRetryable = isUncertainError(discoveryError)
  const precheckRetryable = isUncertainError(precheckError)
  const executionRetryable = isUncertainError(executionError)
  const discoveryData = currentDiscovery && discoveryQuery.data?.id === discoveryOperationId ? discoveryQuery.data : undefined
  const precheckData = currentPrecheck
    ? (precheckQuery.data?.id === precheckOperationId ? precheckQuery.data : precheckDataState) ?? undefined
    : undefined
  const precheckItems = precheckData?.result?.preCheck ?? []
  const precheckPassed = precheckData?.status === 'Succeeded' && !precheckItems.some((item) => item.status === 'Failed')
  const precheckFailed = Boolean(precheckData) && !isActiveRestore(precheckData!.status) && !precheckPassed

  function invalidateExecution() {
    restoreAttemptRef.current = null
    restoreKey.reset()
    setExecutionError(null)
  }

  function invalidatePrecheck() {
    precheckAttemptRef.current = null
    precheckKey.reset()
    setPrecheckOperation(null)
    setPrecheckDataState(null)
    setPrecheckError(null)
    setConfirming(false)
    invalidateExecution()
  }

  function invalidateDiscovery() {
    discoveryAttemptRef.current = null
    discoveryKey.reset()
    setDiscoveryOperation(null)
    setDiscoveryError(null)
    invalidatePrecheck()
  }

  function changeDatabaseName(value: string) {
    invalidatePrecheck()
    setTargetDatabaseName(value)
  }

  function changeOtherTarget(value: RestoreTarget) {
    invalidateDiscovery()
    setTrustCertificate(false)
    setOtherTarget(value)
  }

  async function triggerDiscovery() {
    if (!eligibleForRestore || !targetValid || loginPending || pendingRef.current.discovery) return
    const request = { agentId: selectedAgentId, target: { ...effectiveTarget } }
    const attempt: RequestAttempt<StartRestoreDiscoveryVariables> = {
      fingerprint: discoveryFingerprint,
      variables: { artifactId: artifact.id, idempotencyKey: discoveryKey.keyFor(request), request },
    }
    invalidatePrecheck()
    setDiscoveryOperation(null)
    setDiscoveryError(null)
    discoveryAttemptRef.current = attempt
    pendingRef.current.discovery = true
    try {
      const result = await startDiscovery.mutateAsync(attempt.variables)
      discoveryKey.settle()
      if (discoveryAttemptRef.current !== attempt) return
      setDiscoveryOperation({ fingerprint: attempt.fingerprint, id: result.id })
    } catch (error) {
      discoveryKey.settle(error)
      if (discoveryAttemptRef.current !== attempt) return
      setDiscoveryError(error)
    } finally {
      pendingRef.current.discovery = false
    }
  }

  // `trust` lets the trust confirmation check again before the state it sets has rendered.
  async function triggerPrecheck(fresh = false, trust = trustCertificate) {
    if (!eligibleForRestore || !targetValid || loginPending || !databaseNameValid || pendingRef.current.precheck || pendingRef.current.execution) return
    if (fresh) precheckKey.reset()
    const target = targetWithTrust(trust)
    const request = {
      agentId: selectedAgentId,
      target,
      databaseName: targetDatabaseName.trim(),
      mode,
      options: { ...options },
    }
    const attempt: RequestAttempt<StartRestorePrecheckVariables> = {
      fingerprint: precheckFingerprintFor(target),
      variables: { artifactId: artifact.id, idempotencyKey: precheckKey.keyFor(request), request },
    }
    invalidateExecution()
    setPrecheckOperation(null)
    setPrecheckDataState(null)
    setPrecheckError(null)
    precheckAttemptRef.current = attempt
    pendingRef.current.precheck = true
    try {
      const result = await startPrecheck.mutateAsync(attempt.variables)
      precheckKey.settle()
      if (precheckAttemptRef.current !== attempt) return
      setPrecheckDataState(result)
      setPrecheckOperation({ fingerprint: attempt.fingerprint, id: result.id })
    } catch (error) {
      precheckKey.settle(error)
      if (precheckAttemptRef.current !== attempt) return
      setPrecheckError(error)
    } finally {
      pendingRef.current.precheck = false
    }
  }

  async function triggerExecution() {
    if (!eligibleForRestore || !precheckPassed || !currentPrecheck || !precheckOperationId || pendingRef.current.precheck || pendingRef.current.execution) return
    const fingerprint = JSON.stringify([precheckFingerprint, precheckOperationId])
    const request = {
      agentId: selectedAgentId,
      target: { ...effectiveTarget },
      databaseName: targetDatabaseName.trim(),
      mode,
      confirmOverwrite: mode === 'ReplaceExistingDatabase' ? targetDatabaseName.trim() : undefined,
      options: { ...options },
      precheckId: precheckOperationId,
    }
    const attempt: RequestAttempt<StartRestoreVariables> = {
      fingerprint,
      variables: { artifactId: artifact.id, idempotencyKey: restoreKey.keyFor(request), request },
    }
    restoreAttemptRef.current = attempt
    pendingRef.current.execution = true
    setExecutionError(null)
    try {
      const result = await startRestore.mutateAsync(attempt.variables)
      restoreKey.settle()
      if (restoreAttemptRef.current !== attempt) return
      navigate(restorePath(tenantId, result.id))
    } catch (error) {
      restoreKey.settle(error)
      if (restoreAttemptRef.current !== attempt) return
      setExecutionError(error)
    } finally {
      pendingRef.current.execution = false
    }
  }

  function selectNewDatabase() {
    if (mode === 'NewDatabase') return
    invalidateDiscovery()
    setMode('NewDatabase')
    setTargetDatabaseName(newDatabaseName)
  }

  function selectReplaceExisting() {
    if (pendingRef.current.discovery) return
    if (mode !== 'ReplaceExistingDatabase') {
      invalidateDiscovery()
      setMode('ReplaceExistingDatabase')
      setTargetDatabaseName(artifact.databaseName ?? '')
    }
    void triggerDiscovery()
  }

  function continueToConfirm() {
    setConfirming(true)
    if (!(currentPrecheck && precheckPassed)) void triggerPrecheck()
  }

  // Only a passed check that matches the current settings opens the confirmation step.
  const shown = confirming && precheckPassed ? 'confirm' : step
  const checking = confirming && !precheckPassed && precheckError == null
  const checkPanel = checking && (precheckFailed && precheckData
    ? <Section title={tx("Ön kontrol")}><PrecheckResults data={precheckData} failed passed={false} onRecheck={() => void triggerPrecheck(true)} recheckDisabled={startPrecheck.isPending || startRestore.isPending}
      trust={{ note: tx("Bu güven yalnızca bu geri yükleme için geçerlidir; sertifika kaydedilmez."), pending: startPrecheck.isPending, onConfirm: () => { setTrustCertificate(true); void triggerPrecheck(true, true) } }} /></Section>
    : <p role="status" className="flex items-center gap-2 text-sm text-text-muted"><Loader2 aria-hidden="true" className="size-4 animate-spin" />{tx("Ön kontrol çalışıyor…")}</p>)
  const stepLabels = skipSource ? [tx('Nereye ve nasıl'), tx('Onay')] : [tx('Kaynak'), tx('Nereye ve nasıl'), tx('Onay')]
  const current = (shown === 'source' ? 0 : shown === 'target' ? 1 : 2) - (skipSource ? 1 : 0)

  return (
    <div className="space-y-6">
      <Stepper steps={stepLabels} current={current} />
      <RestoreSourceSummary artifact={artifact} isPg={isPg} />

      {shown === 'source' && (
        <RestoreSourceStep
          instancePrefix={instancePrefix}
          engineName={engineName}
          targets={targets}
          activeAgent={activeAgent}
          selectedAgentId={selectedAgentId}
          eligibleForRestore={Boolean(eligibleForRestore)}
          invalidateDiscovery={invalidateDiscovery}
          setSelectedAgentId={(id) => { setTrustCertificate(false); setSelectedAgentId(id) }}
          onContinue={() => setStep('target')}
        />
      )}

      {shown === 'target' && (
        <div className="space-y-5">
        <RestoreTargetStep
          tenantId={tenantId}
          instancePrefix={instancePrefix}
          isPg={isPg}
          engineName={engineName}
          ownTarget={ownTarget}
          selectedAgentId={selectedAgentId}
          targetType={targetType}
          otherTarget={otherTarget}
          invalidateDiscovery={invalidateDiscovery}
          setTargetType={(type) => { setTrustCertificate(false); setTargetType(type) }}
          changeOtherTarget={changeOtherTarget}
          onLoginPendingChange={setLoginPending}
        />
        <RestoreModeStep
          instancePrefix={instancePrefix}
          isPg={isPg}
          mode={mode}
          targetDatabaseName={targetDatabaseName}
          databaseNameValid={databaseNameValid}
          discoveryData={discoveryData}
          discoveryError={discoveryError}
          discoveryQueryError={discoveryQuery.error}
          currentDiscovery={currentDiscovery}
          discoveryRetryable={discoveryRetryable}
          discoveryStarting={startDiscovery.isPending}
          discoveryScanning={startDiscovery.isPending || discoveryQuery.isFetching}
          precheckError={precheckError}
          precheckRetryable={precheckRetryable}
          precheckStarting={startPrecheck.isPending}
          restoreStarting={startRestore.isPending}
          eligibleForRestore={Boolean(eligibleForRestore)}
          targetValid={targetValid}
          loginPending={loginPending}
          checkPanel={checkPanel}
          onSelectNewDatabase={selectNewDatabase}
          onSelectReplaceExisting={selectReplaceExisting}
          onRefreshDiscovery={() => void triggerDiscovery()}
          changeDatabaseName={changeDatabaseName}
          onBack={skipSource ? undefined : () => { setConfirming(false); setStep('source') }}
          onContinue={continueToConfirm}
        />
        </div>
      )}

      {shown === 'confirm' && (
        <RestoreConfirmStep
          isPg={isPg}
          mode={mode}
          targetDatabaseName={targetDatabaseName}
          activeAgent={activeAgent}
          selectedAgentId={selectedAgentId}
          effectiveTarget={effectiveTarget}
          precheckData={precheckData}
          precheckPassed={precheckPassed}
          precheckStarting={startPrecheck.isPending}
          restoreStarting={startRestore.isPending}
          executionError={executionError}
          executionRetryable={executionRetryable}
          eligibleForRestore={Boolean(eligibleForRestore)}
          onRecheck={() => void triggerPrecheck(true)}
          onBack={() => setConfirming(false)}
          onStart={() => void triggerExecution()}
        />
      )}
    </div>
  )
}
