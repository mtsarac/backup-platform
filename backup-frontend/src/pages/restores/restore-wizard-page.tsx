import { useEffect, useId, useMemo, useRef, useState } from 'react'
import type { ReactNode } from 'react'
import { ArrowLeft, CheckCircle2, AlertTriangle, XCircle, RotateCcw } from 'lucide-react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { QueryState } from '@/components/data/query-state'
import { StatusPill } from '@/components/data/status-pill'
import { Button } from '@/components/ui/button'
import { useArtifact } from '@/features/artifacts/hooks'
import type { BackupArtifact } from '@/features/artifacts/types'
import { artifactSize } from '@/features/artifacts/format'
import { useRestore, useRestoreTargets, useStartRestore, useStartRestoreDiscovery, useStartRestorePrecheck } from '@/features/restores/hooks'
import { restoreErrorMessage, restoreFailureMessage, restoreModeLabel, safeRestoreCode } from '@/features/restores/presentation'
import type { RestoreMode, RestoreOptions, RestoreTarget, RestoreTargetsResponse, RestoreView, StartRestoreDiscoveryVariables, StartRestorePrecheckVariables, StartRestoreVariables } from '@/features/restores/types'
import { useTenant } from '@/features/tenancy/use-tenant'
import { ApiError } from '@/lib/api/client'

const inputClass = 'h-9 w-full rounded-md border border-border bg-surface-2 px-3 text-sm outline-none focus:border-ring disabled:opacity-60'
const selectClass = 'h-9 w-full rounded-md border border-border bg-surface-2 px-3 text-sm outline-none focus:border-ring disabled:opacity-60'

function StepBadge({ step, current, title }: { step: number; current: number; title: string }) {
  const isDone = current > step
  const isCurrent = current === step
  return (
    <div className={`flex items-center gap-2 text-sm font-medium ${isCurrent ? 'text-primary' : isDone ? 'text-text' : 'text-text-muted'}`}>
      <span className={`flex size-6 items-center justify-center rounded-full text-xs font-semibold ${isCurrent ? 'bg-primary text-primary-foreground' : isDone ? 'bg-surface-2 text-text border border-border' : 'bg-surface-2 text-text-muted'}`}>
        {step}
      </span>
      <span>{title}</span>
    </div>
  )
}

function Section({ title, description, children }: { title: string; description?: string; children: ReactNode }) {
  return (
    <section className="space-y-4 rounded-lg border-2 border-border bg-surface p-4 sm:p-6">
      <div>
        <h2 className="text-xl font-semibold text-text-heading">{title}</h2>
        {description && <p className="mt-1 text-base text-text-muted">{description}</p>}
      </div>
      {children}
    </section>
  )
}

export function RestoreWizardPage() {
  const { id = '' } = useParams<{ id: string }>()
  const { tenantId, canOperate } = useTenant()
  const artifactQuery = useArtifact(tenantId, id)
  const targetsQuery = useRestoreTargets(id)

  if (!canOperate) {
    return (
      <div className="space-y-4 p-6">
        <Link className="inline-flex items-center gap-1 text-sm text-primary hover:underline" to={`/tenants/${tenantId}/artifacts/${id}`}>
          <ArrowLeft className="size-4" /> Backup detaylarına dön
        </Link>
        <div role="alert" className="rounded-lg border border-error/30 bg-error-subtle p-4 text-sm text-error">
          Bu sayfada geri yükleme (Restore) işlemi yapma yetkiniz yok. Restore başlatma yalnızca Owner, Admin ve Operator rollerine açıktır. Viewer rolü salt okunurdur.
        </div>
      </div>
    )
  }

  return (
    <div className="space-y-6">
      <Link className="inline-flex items-center gap-1 text-sm text-primary hover:underline" to={`/tenants/${tenantId}/artifacts/${id}`}>
        <ArrowLeft className="size-4" /> Backup detaylarına dön
      </Link>
      <header>
        <h1 className="text-3xl font-semibold text-text-heading">Backup Geri Yükleme Sihirbazı</h1>
        <p className="mt-1 text-base text-text-muted">
          Doğrulanmış MSSQL backup'ını hedef SQL Server instance'ına güvenli ve denetlenebilir adımlarla geri yükleyin.
        </p>
      </header>
      <QueryState isPending={artifactQuery.isPending || targetsQuery.isPending} error={artifactQuery.error || targetsQuery.error}>
        {artifactQuery.data && targetsQuery.data && (
          <RestoreWizard
            key={`${tenantId}:${id}`}
            tenantId={tenantId}
            artifact={artifactQuery.data}
            targets={targetsQuery.data}
          />
        )}
      </QueryState>
    </div>
  )
}

interface RestoreWizardProps {
  tenantId: string
  artifact: BackupArtifact
  targets: RestoreTargetsResponse
}

interface RequestAttempt<Variables> {
  readonly fingerprint: string
  readonly variables: Variables
}

function RestoreWizard({ tenantId, artifact, targets }: RestoreWizardProps) {
  const navigate = useNavigate()
  const instancePrefix = useId()
  const defaultTarget = targets.targets[0]
  const defaultAgent = targets.agents.find((a) => a.canRestore && a.sourceEligible) ?? targets.agents[0]

  const [step, setStep] = useState<number>(1)
  const [selectedAgentId, setSelectedAgentId] = useState<string>(defaultAgent?.agentId ?? '')
  const [targetType, setTargetType] = useState<'configured' | 'custom'>(defaultTarget ? 'configured' : 'custom')
  const [selectedTargetIndex, setSelectedTargetIndex] = useState<number>(0)
  const [customTarget, setCustomTarget] = useState<RestoreTarget>({
    host: defaultTarget?.target.host ?? '',
    port: defaultTarget?.target.port ?? null,
    integratedSecurity: defaultTarget?.target.integratedSecurity ?? true,
    databaseCredentialReference: defaultTarget?.target.databaseCredentialReference ?? null,
    encrypt: defaultTarget?.target.encrypt ?? true,
    trustServerCertificate: defaultTarget?.target.trustServerCertificate ?? true,
  })

  const [mode, setMode] = useState<RestoreMode>('NewDatabase')
  const [targetDatabaseName, setTargetDatabaseName] = useState<string>(artifact.databaseName ? `${artifact.databaseName}_restored` : 'RestoredDB')
  const [confirmOverwriteInput, setConfirmOverwriteInput] = useState<string>('')
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
  const precheckWarnings = precheckItems.filter((item) => item.status === 'Warning')
  const replaceConfirmed = mode !== 'ReplaceExistingDatabase' || (confirmOverwriteInput.trim() === targetDatabaseName.trim() && targetDatabaseName.trim().length > 0)

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
    setConfirmOverwriteInput('')

  }

  function changeDatabaseName(value: string) {
    invalidatePrecheck()
    setConfirmOverwriteInput('')
    setTargetDatabaseName(value)
  }

  function changeOptions(value: RestoreOptions) {
    invalidatePrecheck()
    setConfirmOverwriteInput('')
    setOptions(value)
  }

  function changeCustomTarget(value: RestoreTarget) {
    invalidateDiscovery()
    setCustomTarget(value)
  }

  async function triggerDiscovery() {
    if (!eligibleForRestore || !effectiveTarget.host || pendingRef.current.discovery) return
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
    setConfirmOverwriteInput('')

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
    if (!eligibleForRestore || !effectiveTarget.host || !targetDatabaseName.trim() || !replaceConfirmed || pendingRef.current.precheck || pendingRef.current.execution) return
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
      setStep(4)
    } catch (error) {
      if (precheckAttemptRef.current !== attempt) return
      setPrecheckError(error)
    } finally {
      pendingRef.current.precheck = false
    }
  }

  async function triggerExecution() {
    if (!eligibleForRestore || !precheckPassed || !replaceConfirmed || !currentPrecheck || !precheckOperationId || pendingRef.current.precheck || pendingRef.current.execution) return
    const fingerprint = JSON.stringify([precheckFingerprint, precheckOperationId, confirmOverwriteInput.trim()])
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
              confirmOverwrite: mode === 'ReplaceExistingDatabase' ? confirmOverwriteInput.trim() : undefined,
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

  return (
    <div className="space-y-6">
      <nav aria-label="Sihirbaz adımları" className="flex flex-wrap items-center gap-6 rounded-lg border border-border bg-surface p-4">
        <StepBadge step={1} current={step} title="Hedef & Instance" />
        <StepBadge step={2} current={step} title="Restore Modu" />
        <StepBadge step={3} current={step} title="Ön Kontrol (Precheck)" />
        <StepBadge step={4} current={step} title="Onay & Başlat" />
      </nav>

      {/* Artifact Summary */}
      <section aria-label="Kaynak Backup Bilgisi" className="rounded-lg border border-border bg-surface-2 p-4 text-sm">
        <div className="flex flex-wrap items-center justify-between gap-2 font-medium">
          <span>Kaynak: {artifact.databaseName ?? 'Bilinmeyen DB'} · {artifactSize(artifact.sizeBytes)}</span>
          <StatusPill status={artifact.state} />
        </div>
        <p className="mt-1 text-xs text-text-muted">
          Artifact ID: {artifact.id} · Tür: {artifact.backupType} · Replikalar: {artifact.replicas.map((r) => `${r.storageProvider} (${r.status})`).join(', ')}
        </p>
      </section>

      {/* Step 1: Target Agent & SQL Instance */}
      {step === 1 && (
        <div className="space-y-5">
          <Section title="1. Hedef Agent Seçimi" description="Backup dosyasını alıp SQL Server'a aktaracak Agent'ı seçin.">
            <div className="space-y-3">
              <label htmlFor={`${instancePrefix}-agent`} className="block text-sm font-medium">Agent</label>
              <select
                id={`${instancePrefix}-agent`}
                className={selectClass}
                value={selectedAgentId}
                onChange={(e) => { invalidateDiscovery(); setSelectedAgentId(e.target.value) }}
              >
                {targets.agents.map((ag) => (
                  <option key={ag.agentId} value={ag.agentId}>
                    {ag.hostName ?? ag.agentId} ({ag.connectivity}) — {ag.canRestore ? 'Restore destekliyor' : 'Restore desteği yok'}
                  </option>
                ))}
              </select>

              {activeAgent && (
                <div className={`rounded-md p-3 text-sm ${activeAgent.sourceEligible && activeAgent.canRestore ? 'bg-success-subtle text-success' : 'bg-error-subtle text-error'}`}>
                  {activeAgent.sourceEligible && activeAgent.canRestore ? (
                    <div className="flex flex-col gap-1">
                      <div className="flex items-center gap-2">
                        <CheckCircle2 className="size-4 shrink-0" />
                        <span>Bu Agent bu backup için uygun ({activeAgent.sourceAccess === 'AgentLocal' ? 'Yerel Replika' : activeAgent.sourceAccess === 'BackendRelayed' ? 'Uzak Depolama (SFTP / Google Drive Relay)' : 'Merkezi Depolama'}).</span>
                      </div>
                      <p className="ml-6 text-xs text-text-muted">{activeAgent.sourceAccess === 'AgentLocal'
                        ? 'Yerel replika yalnızca onu barındıran hedef Agent tarafından okunabilir; backend yerel dosyayı kontrol edemez.'
                        : "Kaynak erişimi kontrol edilirken, Agent'a devredilmeden önce backend'in okuyabildiği alternatif doğrulanmış replika seçilebilir."} Agent'a devir, staging veya hedef değişikliği sonrasında Restore otomatik yeniden başlatılmaz.</p>
                    </div>
                  ) : (
                    <div className="flex items-start gap-2">
                      <XCircle className="size-4 shrink-0 mt-0.5" />
                      <div>
                        <p className="font-medium">Bu Agent seçilemez veya backup'a erişemez:</p>
                        <p className="mt-1 text-xs">{activeAgent.rejectionMessage ?? restoreFailureMessage(activeAgent.rejectionCode)}</p>
                      </div>
                    </div>
                  )}
                </div>
              )}
            </div>
          </Section>

          <Section title="2. Hedef SQL Instance" description="Geri yüklemenin yapılacağı SQL Server bağlantı referansı.">
            <div className="space-y-4">
              <div className="flex gap-4 text-sm">
                <label className="flex items-center gap-2">
                  <input
                    type="radio"
                    name="targetType"
                    checked={targetType === 'configured'}
                    onChange={() => { invalidateDiscovery(); setTargetType('configured') }}
                    disabled={targets.targets.length === 0}
                  />
                  <span>Kayıtlı İş Bağlantısı ({targets.targets.length})</span>
                </label>
                <label className="flex items-center gap-2">
                  <input
                    type="radio"
                    name="targetType"
                    checked={targetType === 'custom'}
                    onChange={() => { invalidateDiscovery(); setTargetType('custom') }}
                  />
                  <span>Özel Bağlantı</span>
                </label>
              </div>

              {targetType === 'configured' && targets.targets.length > 0 && (
                <div>
                  <label htmlFor={`${instancePrefix}-target-select`} className="block text-sm font-medium">Bağlantı</label>
                  <select
                    id={`${instancePrefix}-target-select`}
                    className={selectClass}
                    value={selectedTargetIndex}
                    onChange={(e) => { invalidateDiscovery(); setSelectedTargetIndex(Number(e.target.value)) }}
                  >
                    {targets.targets.map((t, idx) => (
                      <option key={`${t.jobId}-${idx}`} value={idx}>
                        {t.jobName} · {t.target.host}{t.target.port ? `:${t.target.port}` : ''} ({t.database})
                      </option>
                    ))}
                  </select>
                </div>
              )}

              {targetType === 'custom' && (
                <div className="grid gap-3 sm:grid-cols-2">
                  <div>
                    <label htmlFor={`${instancePrefix}-host`} className="block text-sm font-medium">Host / Instance</label>
                    <input
                      id={`${instancePrefix}-host`}
                      className={inputClass}
                      value={customTarget.host}
                      onChange={(e) => changeCustomTarget({ ...customTarget, host: e.target.value })}
                      placeholder="localhost veya SQLSERVER"
                    />
                  </div>
                  <div>
                    <label htmlFor={`${instancePrefix}-port`} className="block text-sm font-medium">Port (İsteğe bağlı)</label>
                    <input
                      id={`${instancePrefix}-port`}
                      type="number"
                      className={inputClass}
                      value={customTarget.port ?? ''}
                      onChange={(e) => changeCustomTarget({ ...customTarget, port: e.target.value ? Number(e.target.value) : null })}
                      placeholder="1433"
                    />
                  </div>
                  <div className="sm:col-span-2">
                    <label htmlFor={`${instancePrefix}-cred`} className="block text-sm font-medium">Credential Referansı</label>
                    <input
                      id={`${instancePrefix}-cred`}
                      className={inputClass}
                      value={customTarget.databaseCredentialReference ?? ''}
                      onChange={(e) => changeCustomTarget({ ...customTarget, databaseCredentialReference: e.target.value || null })}
                      placeholder="Agent credential referansı (örn. sql_smoke)"
                    />
                  </div>
                  <div className="flex items-center gap-4 sm:col-span-2 text-sm">
                    <label className="flex items-center gap-2">
                      <input
                        type="checkbox"
                        checked={customTarget.encrypt}
                        onChange={(e) => changeCustomTarget({ ...customTarget, encrypt: e.target.checked })}
                      />
                      <span>Bağlantıyı Şifrele</span>
                    </label>
                    <label className="flex items-center gap-2">
                      <input
                        type="checkbox"
                        checked={customTarget.trustServerCertificate}
                        onChange={(e) => changeCustomTarget({ ...customTarget, trustServerCertificate: e.target.checked })}
                      />
                      <span>Sunucu Sertifikasına Güven</span>
                    </label>
                  </div>
                </div>
              )}
            </div>
          </Section>

          <div className="flex justify-end gap-3">
            <Button
              disabled={!eligibleForRestore || !effectiveTarget.host}
              onClick={() => setStep(2)}
            >
              Devam et: Restore Modu →
            </Button>
          </div>
        </div>
      )}

      {/* Step 2: Restore Mode & Database Name */}
      {step === 2 && (
        <div className="space-y-5">
          <Section title="Geri Yükleme Modu ve Hedef Veritabanı" description="Güvenli yeni bir veritabanı oluşturun veya mevcut olanın üzerine yazın.">
            <div className="space-y-4">
              <div className="grid gap-3 sm:grid-cols-2">
                <button
                  type="button"
                  className={`rounded-lg border-2 p-4 text-left transition-colors ${mode === 'NewDatabase' ? 'border-primary bg-primary/5 ring-1 ring-primary' : 'border-border bg-surface'}`}
                  onClick={() => {
                    if (mode === 'NewDatabase') return
                    invalidateDiscovery()
                    setMode('NewDatabase')
                  }}
                >
                  <p className="font-semibold text-text-heading">Yeni bir veritabanı olarak geri yükle (Önerilen · Güvenli)</p>
                  <p className="mt-1 text-base text-text-muted">
                    Mevcut veritabanlarına asla dokunmaz. Belirleyeceğiniz yeni bir adla sıfırdan veritabanı açar.
                  </p>
                </button>

                <button
                  type="button"
                  className={`rounded-lg border-2 p-4 text-left transition-colors ${mode === 'ReplaceExistingDatabase' ? 'border-destructive bg-destructive/5 ring-1 ring-destructive' : 'border-border bg-surface'}`}
                  disabled={startDiscovery.isPending}
                  onClick={() => {
                    if (pendingRef.current.discovery) return
                    if (mode !== 'ReplaceExistingDatabase') {
                      invalidateDiscovery()
                      setMode('ReplaceExistingDatabase')
                    }
                    void triggerDiscovery()
                  }}
                >
                  <p className="font-semibold text-destructive">Mevcut veritabanının üzerine yaz (Riskli)</p>
                  <p className="mt-1 text-base text-text-muted">
                    Hedef sunucudaki mevcut veritabanının üzerine yazar. Eski veriler kalıcı olarak kaybolur!
                  </p>
                </button>
              </div>

              {mode === 'NewDatabase' && (
                <div className="space-y-2">
                  <label htmlFor={`${instancePrefix}-new-dbname`} className="block text-sm font-medium">Yeni Veritabanı Adı</label>
                  <input
                    id={`${instancePrefix}-new-dbname`}
                    className={inputClass}
                    value={targetDatabaseName}
                    onChange={(e) => changeDatabaseName(e.target.value)}
                    placeholder="Yeni veritabanı adı"
                  />
                  <p className="text-xs text-text-muted">Bu ad hedef SQL Server'da henüz var olmamalıdır.</p>
                </div>
              )}

              {mode === 'ReplaceExistingDatabase' && (
                <div className="space-y-4 rounded-lg border border-destructive/30 bg-destructive/5 p-4">
                  <div className="flex items-start gap-2 text-destructive">
                    <AlertTriangle className="size-5 shrink-0 mt-0.5" />
                    <div>
                      <h3 className="font-semibold">DİKKAT: Veri Kaybı Riski!</h3>
                      <p className="text-sm">
                        Bu işlem hedef veritabanının içeriğini tamamen silecektir. İşlem geri alınamaz.
                      </p>
                    </div>
                  </div>

                  <div className="space-y-2">
                    <div className="flex items-center justify-between">
                      <label htmlFor={`${instancePrefix}-replace-db-select`} className="block text-sm font-medium">Üzerine Yazılacak Veritabanı</label>
                      <Button variant="outline" size="sm" onClick={() => void triggerDiscovery()} disabled={startDiscovery.isPending || discoveryQuery.isFetching}>
                        {startDiscovery.isPending || discoveryQuery.isFetching ? 'Taranıyor…' : discoveryRetryable ? 'Tekrar Dene' : 'Sunucudan Yenile'}
                      </Button>
                    </div>

                    {discoveryData?.result?.discovery ? (
                      <select
                        id={`${instancePrefix}-replace-db-select`}
                        className={selectClass}
                        value={targetDatabaseName}
                        onChange={(e) => changeDatabaseName(e.target.value)}
                      >
                        <option value="">Bir veritabanı seçin</option>
                        {discoveryData.result.discovery.databases.map((db) => (
                          <option key={db.name} value={db.name} disabled={db.isSystem}>
                            {db.name} {db.isSystem ? '(Sistem DB - seçilemez)' : `(${db.availability})`}
                          </option>
                        ))}
                      </select>
                    ) : (
                      <input
                        id={`${instancePrefix}-replace-db-select`}
                        className={inputClass}
                        value={targetDatabaseName}
                        onChange={(e) => changeDatabaseName(e.target.value)}
                        placeholder="Üzerine yazılacak veritabanı adı"
                      />
                    )}
                    {(discoveryError != null || (currentDiscovery && discoveryQuery.error)) && (
                      <div role="alert" className="text-sm text-error">{restoreErrorMessage(discoveryError || discoveryQuery.error)}</div>
                    )}
                  </div>

                  <div className="space-y-2">
                    <label htmlFor={`${instancePrefix}-confirm-overwrite`} className="block text-sm font-medium text-destructive">
                      Onaylamak için veritabanı adını (<span className="font-mono font-bold">{targetDatabaseName}</span>) birebir yazın:
                    </label>
                    <input
                      id={`${instancePrefix}-confirm-overwrite`}
                      className={`${inputClass} border-destructive/50 focus:border-destructive`}
                      value={confirmOverwriteInput}
                      onChange={(e) => { invalidateExecution(); setConfirmOverwriteInput(e.target.value) }}
                      placeholder={targetDatabaseName}
                    />
                    {confirmOverwriteInput.length > 0 && confirmOverwriteInput !== targetDatabaseName && (
                      <p className="text-xs text-destructive">Girdiğiniz ad hedef veritabanı adı ile eşleşmiyor.</p>
                    )}
                  </div>
                </div>
              )}

              {/* Advanced Options */}
              <div className="space-y-2 pt-2 border-t border-border">
                <p className="text-sm font-medium text-text-heading">Seçenekler</p>
                <div className="grid gap-2 sm:grid-cols-2 text-sm">
                  <label className="flex items-center gap-2">
                    <input
                      type="checkbox"
                      checked={options.verifyChecksum ?? true}
                      onChange={(e) => changeOptions({ ...options, verifyChecksum: e.target.checked })}
                    />
                    <span>SHA-256 Checksum Doğrula</span>
                  </label>
                  <label className="flex items-center gap-2">
                    <input
                      type="checkbox"
                      checked={options.verifyBackup ?? true}
                      onChange={(e) => changeOptions({ ...options, verifyBackup: e.target.checked })}
                    />
                    <span>RESTORE VERIFYONLY Çalıştır</span>
                  </label>
                  {mode === 'ReplaceExistingDatabase' && (
                    <label className="flex items-center gap-2 sm:col-span-2 text-destructive">
                      <input
                        type="checkbox"
                        checked={options.terminateActiveConnections ?? false}
                        onChange={(e) => changeOptions({ ...options, terminateActiveConnections: e.target.checked })}
                      />
                      <span>Mevcut aktif bağlantıları sonlandır (WITH ROLLBACK IMMEDIATE)</span>
                    </label>
                  )}
                </div>
              </div>
            </div>
          </Section>

          {precheckError != null && (
            <div role="alert" className="rounded-lg border border-error/30 bg-error-subtle p-4 text-sm text-error">{restoreErrorMessage(precheckError)}</div>
          )}

          <div className="flex justify-between gap-3">
            <Button variant="outline" onClick={() => setStep(1)}>← Geri</Button>
            <Button
              disabled={!eligibleForRestore || !effectiveTarget.host || !targetDatabaseName.trim() || !replaceConfirmed || startPrecheck.isPending || startRestore.isPending}
              onClick={() => void triggerPrecheck()}
            >
              {startPrecheck.isPending ? 'Ön Kontrol Başlatılıyor…' : precheckRetryable ? 'Tekrar Dene' : 'Ön Kontrolü Çalıştır →'}
            </Button>
          </div>
        </div>
      )}

      {/* Step 3 & 4: Precheck Results & Execution */}
      {step >= 3 && (
        <div className="space-y-5">
          <Section title="Ön Kontrol (Precheck) Sonuçları" description="Hedef SQL Server üzerinde hiçbir değişiklik yapılmadan çalıştırılan uyumluluk ve güvenlik kontrolleri.">
            <QueryState isPending={(startPrecheck.isPending || (!!precheckOperationId && precheckQuery.isPending)) && !precheckData} error={precheckError || (currentPrecheck && precheckQuery.error)}>
              {precheckData && (
                <div className="space-y-4">
                  <div className="flex flex-wrap items-center justify-between gap-2 border-b border-border pb-3">
                    <div className="flex items-center gap-2">
                      <span className="font-medium">Durum:</span>
                      <StatusPill status={precheckData.status} />
                    </div>
                    <Button variant="outline" size="sm" onClick={() => void triggerPrecheck(true)} disabled={startPrecheck.isPending || startRestore.isPending}>
                      <RotateCcw className="size-3.5 mr-1" /> Yeniden Kontrol Et
                    </Button>
                  </div>

                  {precheckFailed && (
                    <div role="alert" className="rounded-lg border border-error/30 bg-error-subtle p-4 text-sm text-error">
                      <div className="flex items-center gap-2 font-semibold">
                        <XCircle className="size-5 shrink-0" />
                        <span>Ön kontrol başarısız oldu! Restore başlatılamaz.</span>
                      </div>
                      <p className="mt-1 text-xs">
                        {precheckData.reason ?? 'Aşağıdaki kontrollerden en az biri başarısız oldu. Hataları giderdikten sonra tekrar deneyin.'}
                      </p>
                    </div>
                  )}

                  {precheckPassed && precheckWarnings.length > 0 && (
                    <div role="status" className="rounded-lg border border-warning/30 bg-warning-subtle p-4 text-sm text-warning">
                      <div className="flex items-center gap-2 font-semibold">
                        <AlertTriangle className="size-5 shrink-0" />
                        <span>Ön kontrol uyarılarla geçti.</span>
                      </div>
                      <p className="mt-1 text-xs">Aşağıdaki uyarıları inceleyerek devam edebilirsiniz.</p>
                    </div>
                  )}

                  {precheckPassed && precheckWarnings.length === 0 && (
                    <div role="status" className="rounded-lg border border-success/30 bg-success-subtle p-4 text-sm text-success">
                      <div className="flex items-center gap-2 font-semibold">
                        <CheckCircle2 className="size-5 shrink-0" />
                        <span>Tüm ön kontroller başarıyla tamamlandı. Geri yükleme güvenle başlatılabilir.</span>
                      </div>
                    </div>
                  )}

                  {/* Precheck Items List */}
                  <div className="rounded-lg border border-border overflow-hidden">
                    <table className="w-full text-left text-sm">
                      <thead className="bg-surface-2 text-xs font-semibold uppercase text-text-muted">
                        <tr>
                          <th className="p-3">Kontrol</th>
                          <th className="p-3">Durum</th>
                          <th className="p-3">Açıklama</th>
                        </tr>
                      </thead>
                      <tbody className="divide-y divide-border">
                        {precheckItems.map((item, idx) => (
                          <tr key={`${item.checkId}-${idx}`} className="hover:bg-surface-2/50">
                            <td className="p-3 font-mono text-xs">{safeRestoreCode(item.checkId)}</td>
                            <td className="p-3">
                              {item.status === 'Passed' && <span className="inline-flex items-center gap-1 text-xs font-medium text-success"><CheckCircle2 className="size-3.5" /> Geçti</span>}
                              {item.status === 'Warning' && <span className="inline-flex items-center gap-1 text-xs font-medium text-warning"><AlertTriangle className="size-3.5" /> Uyarı</span>}
                              {item.status === 'Failed' && <span className="inline-flex items-center gap-1 text-xs font-medium text-error"><XCircle className="size-3.5" /> Başarısız</span>}
                              {item.status === 'Skipped' && <span className="text-xs text-text-muted">Atlandı</span>}
                            </td>
                            <td className="p-3 text-xs text-text-muted">{item.message || safeRestoreCode(item.reasonCode)}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>

                  {/* Header / Version info */}
                  {precheckData.result?.sourceBackup && (
                    <div className="grid gap-2 sm:grid-cols-2 text-xs rounded-lg border border-border bg-surface-2 p-3">
                      <div>Kaynak DB: <span className="font-semibold">{precheckData.result.sourceBackup.databaseName}</span></div>
                      <div>Hedef SQL Sürümü: <span className="font-semibold">{precheckData.result.targetServer?.version ?? 'Bilinmiyor'}</span></div>
                      <div>Header Doğrulandı: <span className="font-semibold">{precheckData.result.backupVerified ? 'Evet' : 'Hayır'}</span></div>
                      <div>Checksum Doğrulandı: <span className="font-semibold">{precheckData.result.checksumVerified ? 'Evet' : 'Hayır'}</span></div>
                    </div>
                  )}
                </div>
              )}
            </QueryState>
            {precheckError != null && (
              <Button variant="outline" size="sm" onClick={() => void triggerPrecheck()} disabled={startPrecheck.isPending || startRestore.isPending}>
                {precheckRetryable ? 'Tekrar Dene' : 'Yeniden Kontrol Et'}
              </Button>
            )}
          </Section>

          {executionError != null && (
            <div role="alert" className="rounded-lg border border-error/30 bg-error-subtle p-4 text-sm text-error">
              {executionError instanceof ApiError ? restoreErrorMessage(executionError) : 'Geri yükleme başlatılamadı.'}
            </div>
          )}

          <div className="flex justify-between gap-3">
            <Button variant="outline" onClick={() => setStep(2)}>← Mod Ayarlarına Dön</Button>
            <Button
              variant={mode === 'ReplaceExistingDatabase' ? 'destructive' : 'default'}
              disabled={!eligibleForRestore || !precheckPassed || precheckFailed || !replaceConfirmed || startPrecheck.isPending || startRestore.isPending}
              onClick={() => void triggerExecution()}
            >
              {startRestore.isPending ? 'Geri Yükleme Başlatılıyor…' : executionRetryable ? 'Tekrar Dene' : `${restoreModeLabel(mode)} Olarak Restore Başlat`}
            </Button>
          </div>
        </div>
      )}
    </div>
  )
}
