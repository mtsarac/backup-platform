import { tx, useLanguage } from '@/lib/i18n'
import { useEffect, useId, useMemo, useRef, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { Stepper } from '@/components/ui/stepper'
import type { BackupArtifact } from '@/features/artifacts/types'
import { useRestore, useStartRestore, useStartRestoreDiscovery, useStartRestorePrecheck } from './hooks'
import type { RestoreMode, RestoreOptions, RestoreTarget, RestoreTargetsResponse, RestoreView, StartRestoreDiscoveryVariables, StartRestorePrecheckVariables, StartRestoreVariables } from './types'
import { ApiError } from '@/lib/api/client'
import { RestoreSourceSummary } from './restore-source-summary'
import { RestoreTargetStep } from './restore-target-step'
import { RestoreModeStep } from './restore-mode-step'
import { RestoreCheckStep } from './restore-check-step'
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
  const defaultTarget = targets.targets[0]
  const defaultAgent = targets.agents.find((a) => a.canRestore && a.sourceEligible) ?? targets.agents[0]

  const [step, setStep] = useState<number>(1)
  const [selectedAgentId, setSelectedAgentId] = useState<string>(defaultAgent?.agentId ?? '')
  const [targetType, setTargetType] = useState<'configured' | 'custom'>(defaultTarget ? 'configured' : 'custom')
  const [selectedTargetIndex, setSelectedTargetIndex] = useState<number>(0)
  const [customTarget, setCustomTarget] = useState<RestoreTarget>({
    host: defaultTarget?.target.host ?? '',
    port: defaultTarget?.target.port ?? (isPg ? 5432 : null),
    integratedSecurity: defaultTarget?.target.integratedSecurity ?? !isPg,
    databaseCredentialReference: defaultTarget?.target.databaseCredentialReference ?? null,
    encrypt: defaultTarget?.target.encrypt ?? true,
    trustServerCertificate: defaultTarget?.target.trustServerCertificate ?? false,
  })

  const [mode, setMode] = useState<RestoreMode>('NewDatabase')
  const [targetDatabaseName, setTargetDatabaseName] = useState<string>(() => {
    const name = artifact.databaseName ? `${artifact.databaseName}_restored` : 'RestoredDB'
    return isPg && new TextEncoder().encode(name).length > 63 ? 'restored_db' : name
  })
  const [options, setOptions] = useState<RestoreOptions>({
    terminateActiveConnections: false,
    verifyChecksum: true,
    verifyBackup: true,
  })

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
  const pendingRef = useRef({ discovery: false, precheck: false, execution: false })
  const [executionError, setExecutionError] = useState<unknown>(null)

  useEffect(() => () => {
    discoveryAttemptRef.current = null
    precheckAttemptRef.current = null
    restoreAttemptRef.current = null
  }, [])

  const activeAgent = targets.agents.find((a) => a.agentId === selectedAgentId)
  const effectiveTarget = useMemo<RestoreTarget>(() => {
    if (targetType === 'configured' && targets.targets[selectedTargetIndex]) {
      return targets.targets[selectedTargetIndex].target
    }
    return customTarget
  }, [targetType, targets.targets, selectedTargetIndex, customTarget])

  const eligibleForRestore = artifact.state === 'Available' && activeAgent?.canRestore && activeAgent?.sourceEligible
  const targetValid = Boolean(effectiveTarget.host) && (!isPg || (!effectiveTarget.integratedSecurity && Boolean(effectiveTarget.databaseCredentialReference)))
  const databaseNameValid = Boolean(targetDatabaseName.trim()) && (!isPg || new TextEncoder().encode(targetDatabaseName.trim()).length <= 63)

  const discoveryFingerprint = JSON.stringify([selectedAgentId, effectiveTarget])
  const precheckFingerprint = JSON.stringify([discoveryFingerprint, targetDatabaseName.trim(), mode, options])
  const currentDiscovery = discoveryOperation?.fingerprint === discoveryFingerprint
  const currentPrecheck = precheckOperation?.fingerprint === precheckFingerprint
  const discoveryRetryable = discoveryError != null && (!(discoveryError instanceof ApiError) || discoveryError.status >= 500)
  const precheckRetryable = precheckError != null && (!(precheckError instanceof ApiError) || precheckError.status >= 500)
  const executionRetryable = executionError != null && (!(executionError instanceof ApiError) || executionError.status >= 500)
  const discoveryData = currentDiscovery && discoveryQuery.data?.id === discoveryOperationId ? discoveryQuery.data : undefined
  const precheckData = currentPrecheck
    ? (precheckQuery.data?.id === precheckOperationId ? precheckQuery.data : precheckDataState) ?? undefined
    : undefined
  const precheckItems = precheckData?.result?.preCheck ?? []
  const precheckFailed = precheckData?.status === 'Failed' || precheckItems.some((item) => item.status === 'Failed')
  const precheckPassed = precheckData?.status === 'Succeeded' && !precheckFailed

  function invalidateExecution() {
    restoreAttemptRef.current = null
    setExecutionError(null)
  }

  function invalidatePrecheck() {
    precheckAttemptRef.current = null
    setPrecheckOperation(null)
    setPrecheckDataState(null)
    setPrecheckError(null)
    invalidateExecution()
  }

  function invalidateDiscovery() {
    discoveryAttemptRef.current = null
    setDiscoveryOperation(null)
    setDiscoveryError(null)
    invalidatePrecheck()
  }

  function changeDatabaseName(value: string) {
    invalidatePrecheck()
    setTargetDatabaseName(value)
  }

  function changeOptions(value: RestoreOptions) {
    invalidatePrecheck()
    setOptions(value)
  }

  function changeCustomTarget(value: RestoreTarget) {
    invalidateDiscovery()
    setCustomTarget(value)
  }

  async function triggerDiscovery() {
    if (!eligibleForRestore || !targetValid || pendingRef.current.discovery) return
    const previous = discoveryAttemptRef.current
    const attempt: RequestAttempt<StartRestoreDiscoveryVariables> = previous && discoveryRetryable && previous.fingerprint === discoveryFingerprint
      ? previous
      : {
          fingerprint: discoveryFingerprint,
          variables: {
            artifactId: artifact.id,
            idempotencyKey: crypto.randomUUID(),
            request: { agentId: selectedAgentId, target: { ...effectiveTarget } },
          },
        }
    invalidatePrecheck()
    setDiscoveryOperation(null)
    setDiscoveryError(null)
    discoveryAttemptRef.current = attempt
    pendingRef.current.discovery = true
    try {
      const result = await startDiscovery.mutateAsync(attempt.variables)
      if (discoveryAttemptRef.current !== attempt) return
      setDiscoveryOperation({ fingerprint: attempt.fingerprint, id: result.id })
    } catch (error) {
      if (discoveryAttemptRef.current !== attempt) return
      setDiscoveryError(error)
    } finally {
      pendingRef.current.discovery = false
    }
  }

  async function triggerPrecheck(fresh = false) {
    if (!eligibleForRestore || !targetValid || !databaseNameValid || pendingRef.current.precheck || pendingRef.current.execution) return
    const previous = precheckAttemptRef.current
    const attempt: RequestAttempt<StartRestorePrecheckVariables> = !fresh && previous && precheckRetryable && previous.fingerprint === precheckFingerprint
      ? previous
      : {
          fingerprint: precheckFingerprint,
          variables: {
            artifactId: artifact.id,
            idempotencyKey: crypto.randomUUID(),
            request: {
              agentId: selectedAgentId,
              target: { ...effectiveTarget },
              databaseName: targetDatabaseName.trim(),
              mode,
              options: { ...options },
            },
          },
        }
    invalidateExecution()
    setPrecheckOperation(null)
    setPrecheckDataState(null)
    setPrecheckError(null)
    precheckAttemptRef.current = attempt
    pendingRef.current.precheck = true
    try {
      const result = await startPrecheck.mutateAsync(attempt.variables)
      if (precheckAttemptRef.current !== attempt) return
      setPrecheckDataState(result)
      setPrecheckOperation({ fingerprint: attempt.fingerprint, id: result.id })
      setStep(3)
    } catch (error) {
      if (precheckAttemptRef.current !== attempt) return
      setPrecheckError(error)
    } finally {
      pendingRef.current.precheck = false
    }
  }

  async function triggerExecution() {
    if (!eligibleForRestore || !precheckPassed || !currentPrecheck || !precheckOperationId || pendingRef.current.precheck || pendingRef.current.execution) return
    const fingerprint = JSON.stringify([precheckFingerprint, precheckOperationId])
    const previous = restoreAttemptRef.current
    const attempt: RequestAttempt<StartRestoreVariables> = previous && executionRetryable && previous.fingerprint === fingerprint
      ? previous
      : {
          fingerprint,
          variables: {
            artifactId: artifact.id,
            idempotencyKey: crypto.randomUUID(),
            request: {
              agentId: selectedAgentId,
              target: { ...effectiveTarget },
              databaseName: targetDatabaseName.trim(),
              mode,
              confirmOverwrite: mode === 'ReplaceExistingDatabase' ? targetDatabaseName.trim() : undefined,
              options: { ...options },
              precheckId: precheckOperationId,
            },
          },
        }
    restoreAttemptRef.current = attempt
    pendingRef.current.execution = true
    setExecutionError(null)
    try {
      const result = await startRestore.mutateAsync(attempt.variables)
      if (restoreAttemptRef.current !== attempt) return
      navigate(`/tenants/${tenantId}/restores/${result.id}`)
    } catch (error) {
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
  }

  function selectReplaceExisting() {
    if (pendingRef.current.discovery) return
    if (mode !== 'ReplaceExistingDatabase') {
      invalidateDiscovery()
      setMode('ReplaceExistingDatabase')
    }
    void triggerDiscovery()
  }

  // Only a passed check that matches the current settings opens the confirmation step.
  const shownStep = step === 4 && !precheckPassed ? 3 : step

  return (
    <div className="space-y-6">
      <Stepper steps={[tx('Nereye'), tx('Nasıl'), tx('Kontrol'), tx('Onay')]} current={shownStep - 1} />
      <RestoreSourceSummary artifact={artifact} isPg={isPg} />

      {shownStep === 1 && (
        <RestoreTargetStep
          tenantId={tenantId}
          instancePrefix={instancePrefix}
          isPg={isPg}
          engineName={engineName}
          targets={targets}
          activeAgent={activeAgent}
          selectedAgentId={selectedAgentId}
          targetType={targetType}
          selectedTargetIndex={selectedTargetIndex}
          customTarget={customTarget}
          eligibleForRestore={Boolean(eligibleForRestore)}
          targetValid={targetValid}
          invalidateDiscovery={invalidateDiscovery}
          setSelectedAgentId={setSelectedAgentId}
          setTargetType={setTargetType}
          setSelectedTargetIndex={setSelectedTargetIndex}
          changeCustomTarget={changeCustomTarget}
          onContinue={() => setStep(2)}
        />
      )}

      {shownStep === 2 && (
        <RestoreModeStep
          instancePrefix={instancePrefix}
          isPg={isPg}
          mode={mode}
          targetDatabaseName={targetDatabaseName}
          databaseNameValid={databaseNameValid}
          options={options}
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
          onSelectNewDatabase={selectNewDatabase}
          onSelectReplaceExisting={selectReplaceExisting}
          onRefreshDiscovery={() => void triggerDiscovery()}
          changeDatabaseName={changeDatabaseName}
          changeOptions={changeOptions}
          onBack={() => setStep(1)}
          onRunPrecheck={() => void triggerPrecheck()}
        />
      )}

      {shownStep === 3 && (
        <RestoreCheckStep
          engineName={engineName}
          precheckData={precheckData}
          precheckLoading={startPrecheck.isPending || (!!precheckOperationId && precheckQuery.isPending)}
          precheckDisplayError={precheckError || (currentPrecheck && precheckQuery.error)}
          precheckError={precheckError}
          precheckRetryable={precheckRetryable}
          precheckFailed={precheckFailed}
          precheckPassed={precheckPassed}
          precheckStarting={startPrecheck.isPending}
          restoreStarting={startRestore.isPending}
          onRunPrecheck={(fresh) => void triggerPrecheck(fresh)}
          onBack={() => setStep(2)}
          onContinue={() => setStep(4)}
        />
      )}

      {shownStep === 4 && (
        <RestoreConfirmStep
          isPg={isPg}
          artifact={artifact}
          mode={mode}
          targetDatabaseName={targetDatabaseName}
          activeAgent={activeAgent}
          selectedAgentId={selectedAgentId}
          effectiveTarget={effectiveTarget}
          precheckPassed={precheckPassed}
          precheckStarting={startPrecheck.isPending}
          restoreStarting={startRestore.isPending}
          executionError={executionError}
          executionRetryable={executionRetryable}
          eligibleForRestore={Boolean(eligibleForRestore)}
          onBack={() => setStep(3)}
          onStart={() => void triggerExecution()}
        />
      )}
    </div>
  )
}
