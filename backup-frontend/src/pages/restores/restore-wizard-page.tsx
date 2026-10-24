import { tx, useLanguage } from '@/lib/i18n'
import { useEffect, useId, useMemo, useRef, useState } from 'react'
import { CheckCircle2, XCircle } from 'lucide-react'
import { useNavigate, useParams } from 'react-router-dom'
import { QueryState } from '@/components/data/query-state'
import { StatusPill } from '@/components/data/status-pill'
import { Button } from '@/components/ui/button'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { Fact, Facts, Section } from '@/components/ui/section'
import { Stepper } from '@/components/ui/stepper'
import { inputClass, selectClass } from '@/features/platform/styles'
import { PrecheckResults } from '@/features/restores/precheck-results'
import { useArtifact } from '@/features/artifacts/hooks'
import type { BackupArtifact } from '@/features/artifacts/types'
import { useRestore, useRestoreTargets, useStartRestore, useStartRestoreDiscovery, useStartRestorePrecheck } from '@/features/restores/hooks'
import { restoreModeLabel } from '@/features/restores/presentation'
import type { RestoreMode, RestoreOptions, RestoreTarget, RestoreTargetsResponse, RestoreView, StartRestoreDiscoveryVariables, StartRestorePrecheckVariables, StartRestoreVariables } from '@/features/restores/types'
import { useTenant } from '@/features/tenancy/use-tenant'
import { StoredLoginPanel } from '@/features/restores/stored-login-panel'
import { ApiError } from '@/lib/api/client'
import { FileRestoreWizard } from '@/features/restores/file-restore-wizard'
import { formatBytes } from '@/lib/format'
import { AccessDenied } from '@/components/auth/access-denied'
import { errorMessage, failureMessage } from '@/lib/api/failure-messages'


export function RestoreWizardPage() {
  useLanguage()
  const { id = '' } = useParams<{ id: string }>()
  const { tenantId, canOperate } = useTenant()
  const artifactQuery = useArtifact(tenantId, id)
  const targetsQuery = useRestoreTargets(id)

  if (!canOperate) {
    return <div className="space-y-6">
      <PageHeader title={tx("Backup Geri Yükleme Sihirbazı")} crumbs={[{ label: tx('Backup dosyaları'), to: `/tenants/${tenantId}/artifacts` }, { label: tx('Backup detayları'), to: `/tenants/${tenantId}/artifacts/${id}` }, { label: tx('Geri yükleme') }]} />
      <AccessDenied what={tx("Bu sayfada geri yükleme (Restore) işlemi yapma yetkiniz yok. Restore başlatma yalnızca Owner, Admin ve Operator rollerine açıktır. Viewer rolü salt okunurdur.")} />
    </div>
  }

  return (
    <div className="space-y-6">
      <PageHeader title={tx("Backup Geri Yükleme Sihirbazı")}
        description={targetsQuery.data?.engine === 'file-system' ? tx('Dosya arşivini özgün veya alternatif konuma, tümüyle ya da seçili yollarla geri yükleyin.') : targetsQuery.data?.engine === 'postgres' ? tx("PostgreSQL pg_dump custom arşivini pg_restore ile hedef PostgreSQL sunucusuna geri yükleyin.") : tx("Doğrulanmış backup'ı aynı Database motoruna güvenli ve denetlenebilir adımlarla geri yükleyin.")}
        crumbs={[{ label: tx('Backup dosyaları'), to: `/tenants/${tenantId}/artifacts` }, { label: artifactQuery.data?.databaseName ?? tx('Backup detayları'), to: `/tenants/${tenantId}/artifacts/${id}` }, { label: tx('Geri yükleme') }]}
      />
      <QueryState isPending={artifactQuery.isPending || targetsQuery.isPending} error={artifactQuery.error || targetsQuery.error}>
        {artifactQuery.data && targetsQuery.data && (
          targetsQuery.data.engine === 'file-system' ? <FileRestoreWizard key={`${tenantId}:${id}`} tenantId={tenantId} artifact={artifactQuery.data} targets={targetsQuery.data} /> : <RestoreWizard
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
    if (!eligibleForRestore || !targetValid || !databaseNameValid || !replaceConfirmed || pendingRef.current.precheck || pendingRef.current.execution) return
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
      <Stepper steps={[tx('Hedef ve Instance'), tx('Restore modu'), tx('Ön kontrol (Precheck)'), tx('Onay ve başlat')]} current={step - 1} />

      {/* Artifact Summary */}
      <section aria-label={tx("Kaynak Backup Bilgisi")} className="space-y-2 rounded-xl border border-border bg-surface-2 p-4 text-sm">
        <div className="flex flex-wrap items-center justify-between gap-2 font-medium">
          <span className="min-w-0 break-words">{tx("Kaynak:")}{' '}{artifact.databaseName ?? tx('Bilinmeyen Database')} · {formatBytes(artifact.sizeBytes)}</span>
          <StatusPill status={artifact.state} />
        </div>
        <details>
          <summary className="cursor-pointer text-text-muted">{tx("Kaynak ayrıntıları ·")}{' '}{artifact.replicas.length}{' '}{tx("Replica")}</summary>
          <p className="mt-2 break-all text-sm text-text-muted">{tx("Artifact ID:")}{' '}<code>{artifact.id}</code>{' '}{tx("· Tür:")}{' '}{tx(artifact.backupType)}{' '}{tx("· Replikalar:")}{' '}{artifact.replicas.map((r) => `${r.storageProvider} (${r.status})`).join(', ')}</p>
        </details>
      </section>
      {isPg && <Notice tone="info" title={tx("PostgreSQL Full Restore")}>{tx("Yalnızca güvendiğiniz platform arşivlerini geri yükleyin: pg_restore içindeki SQL, fonksiyonlar ve extension işlemleri hedefte superuser olarak çalışabilir. SHA-256 byte bütünlüğünü doğrular, kaynak SQL'in güvenliğini doğrulamaz. pg_dump cluster rollerini yedeklemez; hedef superuser kimlik bilgisi, kaynak sahiplikleri/ACL için gereken roller ve extension paketleri hazır olmalıdır. Hedef sürümü hem kaynak hem pg_dump sürümüyle aynı veya daha yeni olmalıdır. Önce geçici Database'e geri yüklenir, sonra hedef adı değiştirilir. Onaylı üzerine yazmada eski Database bağlantıları kapalı bp_old_* adıyla kurtarma için korunur; kontrol ve temizliği operatör yapmalıdır. WAL/PITR desteklenmez.")}</Notice>}

      {/* Step 1: Target Agent & SQL Instance */}
      {step === 1 && (
        <div className="space-y-5">
          <Section title={tx("1. Hedef Agent Seçimi")} description={tx("Backup dosyasını alıp {0} sunucusuna aktaracak Agent'ı seçin.", { "0": engineName })}>
            <div className="space-y-3">
              <label htmlFor={`${instancePrefix}-agent`} className="block text-sm font-medium">{tx("Agent")}</label>
              <select
                id={`${instancePrefix}-agent`}
                className={`${selectClass} w-full`}
                value={selectedAgentId}
                onChange={(e) => { invalidateDiscovery(); setSelectedAgentId(e.target.value) }}
              >
                {targets.agents.map((ag) => (
                  <option key={ag.agentId} value={ag.agentId}>
                    {ag.hostName ?? ag.agentId} ({tx(ag.connectivity)}) — {ag.canRestore ? tx('Restore destekliyor') : tx('Restore desteği yok')}
                  </option>
                ))}
              </select>

              {activeAgent && (
                <div className={`rounded-md p-3 text-sm ${activeAgent.sourceEligible && activeAgent.canRestore ? 'bg-success-subtle text-success' : 'bg-error-subtle text-error'}`}>
                  {activeAgent.sourceEligible && activeAgent.canRestore ? (
                    <div className="flex flex-col gap-1">
                      <div className="flex items-center gap-2">
                        <CheckCircle2 className="size-4 shrink-0" />
                        <span>{tx("Bu Agent bu backup için uygun (")}{activeAgent.sourceAccess === 'AgentLocal' ? tx('Yerel Replika') : activeAgent.sourceAccess === 'BackendRelayed' ? tx('Uzak Depolama (SFTP / Google Drive Relay)') : tx('Merkezi Depolama')}).</span>
                      </div>
                      <p className="ml-6 text-xs text-text-muted">{activeAgent.sourceAccess === 'AgentLocal'
                        ? tx('Yerel replika yalnızca onu barındıran hedef Agent tarafından okunabilir; backend yerel dosyayı kontrol edemez.')
                        : tx("Kaynak erişimi kontrol edilirken, Agent'a devredilmeden önce backend'in okuyabildiği alternatif doğrulanmış replika seçilebilir.")}{' '}{tx("Agent'a devir, staging veya hedef değişikliği sonrasında Restore otomatik yeniden başlatılmaz.")}</p>
                    </div>
                  ) : (
                    <div className="flex items-start gap-2">
                      <XCircle className="size-4 shrink-0 mt-0.5" />
                      <div>
                        <p className="font-medium">{tx("Bu Agent seçilemez veya backup'a erişemez:")}</p>
                        <p className="mt-1 text-sm">{failureMessage(activeAgent.rejectionCode, 'restore')}</p>
                      </div>
                    </div>
                  )}
                </div>
              )}
            </div>
          </Section>

          <Section title={tx("2. Hedef {0}", { "0": engineName })} description={tx("Geri yüklemenin yapılacağı {0} bağlantı referansı.", { "0": engineName })}>
            <div className="space-y-4">
              <div className="flex flex-wrap gap-4 text-sm">
                <label className="flex items-center gap-2">
                  <input
                    type="radio"
                    name="targetType"
                    checked={targetType === 'configured'}
                    onChange={() => { invalidateDiscovery(); setTargetType('configured') }}
                    disabled={targets.targets.length === 0}
                  />
                  <span>{tx("Kayıtlı İş Bağlantısı (")}{targets.targets.length})</span>
                </label>
                <label className="flex items-center gap-2">
                  <input
                    type="radio"
                    name="targetType"
                    checked={targetType === 'custom'}
                    onChange={() => { invalidateDiscovery(); setTargetType('custom') }}
                  />
                  <span>{tx("Özel Bağlantı")}</span>
                </label>
              </div>

              {targetType === 'configured' && targets.targets.length > 0 && (
                <div>
                  <label htmlFor={`${instancePrefix}-target-select`} className="block text-sm font-medium">{tx("Bağlantı")}</label>
                  <select
                    id={`${instancePrefix}-target-select`}
                    className={`${selectClass} w-full`}
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
                    <label htmlFor={`${instancePrefix}-host`} className="block text-sm font-medium">{isPg ? tx('PostgreSQL Host') : tx('Host / Instance')}</label>
                    <input
                      id={`${instancePrefix}-host`}
                      className={inputClass}
                      value={customTarget.host}
                      onChange={(e) => changeCustomTarget({ ...customTarget, host: e.target.value })}
                      placeholder={isPg ? 'localhost' : tx('localhost veya SQLSERVER')}
                    />
                  </div>
                  <div>
                    <label htmlFor={`${instancePrefix}-port`} className="block text-sm font-medium">{tx("Port (İsteğe bağlı)")}</label>
                    <input
                      id={`${instancePrefix}-port`}
                      type="number"
                      className={inputClass}
                      value={customTarget.port ?? ''}
                      onChange={(e) => changeCustomTarget({ ...customTarget, port: e.target.value ? Number(e.target.value) : null })}
                      placeholder={isPg ? '5432' : '1433'}
                    />
                  </div>
                  {!customTarget.integratedSecurity && <StoredLoginPanel tenantId={tenantId} agentId={selectedAgentId} idPrefix={`${instancePrefix}-login`} reference={customTarget.databaseCredentialReference} onStored={(reference) => changeCustomTarget({ ...customTarget, databaseCredentialReference: reference })} />}
                  <div className="flex flex-wrap items-center gap-4 text-sm sm:col-span-2">
                                        <label className="flex items-center gap-2">
                      <input type="checkbox" checked={customTarget.integratedSecurity} onChange={(e) => changeCustomTarget({ ...customTarget, integratedSecurity: e.target.checked, databaseCredentialReference: null })} />
                      <span>{tx("Windows kimlik doğrulaması")}</span>
                    </label>
                    <label className="flex items-center gap-2">
                      <input
                        type="checkbox"
                        checked={customTarget.encrypt}
                        onChange={(e) => changeCustomTarget({ ...customTarget, encrypt: e.target.checked })}
                      />
                      <span>{tx("Bağlantıyı Şifrele")}</span>
                    </label>
                    <label className="flex items-center gap-2">
                      <input
                        type="checkbox"
                        checked={customTarget.trustServerCertificate}
                        onChange={(e) => changeCustomTarget({ ...customTarget, trustServerCertificate: e.target.checked })}
                      />
                      <span>{tx("Sunucu Sertifikasına Güven")}</span>
                    </label>
                  </div>
                </div>
              )}
            </div>
          </Section>

          <div className="flex justify-end gap-3">
            <Button
              disabled={!eligibleForRestore || !targetValid}
              onClick={() => setStep(2)}
            >{tx("Devam et: Restore Modu →")}</Button>
          </div>
        </div>
      )}

      {/* Step 2: Restore Mode & Database Name */}
      {step === 2 && (
        <div className="space-y-5">
          <Section title={tx("Geri Yükleme Modu ve Hedef Veritabanı")} description={tx("Güvenli yeni bir veritabanı oluşturun veya mevcut olanın üzerine yazın.")}>
            <div className="space-y-4">
              <div className="grid gap-3 sm:grid-cols-2">
                <button
                  type="button"
                  aria-pressed={mode === 'NewDatabase'}
                  className={`rounded-lg border-2 p-4 text-left transition-colors ${mode === 'NewDatabase' ? 'border-primary bg-primary/5 ring-1 ring-primary' : 'border-border bg-surface'}`}
                  onClick={() => {
                    if (mode === 'NewDatabase') return
                    invalidateDiscovery()
                    setMode('NewDatabase')
                  }}
                >
                  <p className="font-semibold text-text-heading">{tx("Yeni bir veritabanı olarak geri yükle (Önerilen · Güvenli)")}</p>
                  <p className="mt-1 text-base text-text-muted">{tx("Mevcut veritabanlarına asla dokunmaz. Belirleyeceğiniz yeni bir adla sıfırdan veritabanı açar.")}</p>
                </button>

                <button
                  type="button"
                  aria-pressed={mode === 'ReplaceExistingDatabase'}
                  className={`rounded-lg border-2 p-4 text-left transition-colors ${mode === 'ReplaceExistingDatabase' ? 'border-error bg-error-subtle ring-1 ring-error' : 'border-border bg-surface'}`}
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
                  <p className="font-semibold text-error">{tx("Mevcut veritabanının üzerine yaz (Riskli)")}</p>
                  <p className="mt-1 text-base text-text-muted">
                    {isPg ? tx('Hedef Database değiştirilir; eski Database bp_old_* adıyla kurtarma için korunur. Operatör temizliği gerekir.') : tx('Hedef sunucudaki mevcut veritabanının üzerine yazar. Eski veriler kalıcı olarak kaybolur!')}
                  </p>
                </button>
              </div>

              {mode === 'NewDatabase' && (
                <div className="space-y-2">
                  <label htmlFor={`${instancePrefix}-new-dbname`} className="block text-sm font-medium">{tx("Yeni Veritabanı Adı")}</label>
                  <input
                    id={`${instancePrefix}-new-dbname`}
                    className={inputClass}
                    value={targetDatabaseName}
                    onChange={(e) => changeDatabaseName(e.target.value)}
                    placeholder={tx("Yeni veritabanı adı")}
                  />
                  <p className="text-xs text-text-muted">{tx("Bu ad hedef")}{' '}{engineName}{' '}{tx("sunucusunda henüz var olmamalıdır.")}{isPg && tx(' En fazla 63 UTF-8 byte.')}</p>
                  {!databaseNameValid && isPg && <p role="alert" className="text-xs text-error">{tx("PostgreSQL Database adı zorunludur ve en fazla 63 UTF-8 byte olabilir.")}</p>}
                </div>
              )}

              {mode === 'ReplaceExistingDatabase' && (
                <div className="space-y-4 rounded-xl border border-error/40 bg-surface p-4">
                  <Notice tone="warning" title={tx("DİKKAT: Veri Kaybı Riski!")}>
                    {isPg ? tx('Hedef Database değiştirilecektir. Eski Database bağlantıları kapalı bp_old_* adıyla korunur; kurtarma ve temizlik operatörün sorumluluğundadır.') : tx('Bu işlem hedef veritabanının içeriğini tamamen silecektir. İşlem geri alınamaz.')}
                  </Notice>

                  <div className="space-y-2">
                    <div className="flex flex-wrap items-center justify-between gap-2">
                      <label htmlFor={`${instancePrefix}-replace-db-select`} className="block text-sm font-medium">{tx("Üzerine Yazılacak Veritabanı")}</label>
                      <Button variant="outline" size="sm" onClick={() => void triggerDiscovery()} disabled={startDiscovery.isPending || discoveryQuery.isFetching}>
                        {startDiscovery.isPending || discoveryQuery.isFetching ? tx('Taranıyor…') : discoveryRetryable ? tx('Tekrar Dene') : tx('Sunucudan Yenile')}
                      </Button>
                    </div>

                    {discoveryData?.result?.discovery ? (
                      <select
                        id={`${instancePrefix}-replace-db-select`}
                        className={`${selectClass} w-full`}
                        value={targetDatabaseName}
                        onChange={(e) => changeDatabaseName(e.target.value)}
                      >
                        <option value="">{tx("Bir veritabanı seçin")}</option>
                        {discoveryData.result.discovery.databases.map((db) => (
                          <option key={db.name} value={db.name} disabled={db.isSystem}>
                            {db.name} {db.isSystem ? tx('(Sistem DB - seçilemez)') : `(${db.availability})`}
                          </option>
                        ))}
                      </select>
                    ) : (
                      <input
                        id={`${instancePrefix}-replace-db-select`}
                        className={inputClass}
                        value={targetDatabaseName}
                        onChange={(e) => changeDatabaseName(e.target.value)}
                        placeholder={tx("Üzerine yazılacak veritabanı adı")}
                      />
                    )}
                    {(discoveryError != null || (currentDiscovery && discoveryQuery.error)) && (
                      <div role="alert" className="text-sm text-error">{errorMessage(discoveryError || discoveryQuery.error, 'restore')}</div>
                    )}
                  </div>

                  <div className="space-y-2">
                    <label htmlFor={`${instancePrefix}-confirm-overwrite`} className="block break-words text-sm font-medium text-error">{tx("Onaylamak için veritabanı adını (")}<span className="break-all font-mono font-bold">{targetDatabaseName}</span>{tx(") birebir yazın:")}</label>
                    <input
                      id={`${instancePrefix}-confirm-overwrite`}
                      className={`${inputClass} border-error/50`}
                      value={confirmOverwriteInput}
                      onChange={(e) => { invalidateExecution(); setConfirmOverwriteInput(e.target.value) }}
                      placeholder={targetDatabaseName}
                    />
                    {confirmOverwriteInput.length > 0 && confirmOverwriteInput !== targetDatabaseName && (
                      <p className="text-sm text-error">{tx("Girdiğiniz ad hedef veritabanı adı ile eşleşmiyor.")}</p>
                    )}
                  </div>
                </div>
              )}

              {/* Advanced Options */}
              <div className="space-y-2 pt-2 border-t border-border">
                <p className="text-sm font-medium text-text-heading">{tx("Seçenekler")}</p>
                <div className="grid gap-2 sm:grid-cols-2 text-sm">
                  <label className="flex items-center gap-2">
                    <input
                      type="checkbox"
                      checked={options.verifyChecksum ?? true}
                      onChange={(e) => changeOptions({ ...options, verifyChecksum: e.target.checked })}
                    />
                    <span>{tx("SHA-256 Checksum Doğrula")}</span>
                  </label>
                  <label className="flex items-center gap-2">
                    <input
                      type="checkbox"
                      checked={options.verifyBackup ?? true}
                      onChange={(e) => changeOptions({ ...options, verifyBackup: e.target.checked })}
                    />
                    <span>{isPg ? tx('pg_restore Arşiv Doğrulaması') : tx('RESTORE VERIFYONLY Çalıştır')}</span>
                  </label>
                  {mode === 'ReplaceExistingDatabase' && (
                    <label className="flex items-center gap-2 text-error sm:col-span-2">
                      <input
                        type="checkbox"
                        checked={options.terminateActiveConnections ?? false}
                        onChange={(e) => changeOptions({ ...options, terminateActiveConnections: e.target.checked })}
                      />
                      <span>{isPg ? tx('Mevcut aktif bağlantıları sonlandır (pg_terminate_backend)') : tx('Mevcut aktif bağlantıları sonlandır (WITH ROLLBACK IMMEDIATE)')}</span>
                    </label>
                  )}
                </div>
              </div>
            </div>
          </Section>

          {precheckError != null && (
            <ErrorNotice error={precheckError} scope="restore" />
          )}

          <div className="flex flex-wrap justify-between gap-3">
            <Button variant="outline" onClick={() => setStep(1)}>{tx("← Geri")}</Button>
            <Button
              disabled={!eligibleForRestore || !targetValid || !databaseNameValid || !replaceConfirmed || startPrecheck.isPending || startRestore.isPending}
              onClick={() => void triggerPrecheck()}
            >
              {startPrecheck.isPending ? tx('Ön Kontrol Başlatılıyor…') : precheckRetryable ? tx('Tekrar Dene') : tx('Ön Kontrolü Çalıştır →')}
            </Button>
          </div>
        </div>
      )}

      {/* Step 3 & 4: Precheck Results & Execution */}
      {step >= 3 && (
        <div className="space-y-5">
          <Section title={tx("Ön Kontrol (Precheck) Sonuçları")} description={tx("Hedef {0} üzerinde hiçbir değişiklik yapılmadan çalıştırılan uyumluluk ve güvenlik kontrolleri.", { "0": engineName })}>
            <QueryState isPending={(startPrecheck.isPending || (!!precheckOperationId && precheckQuery.isPending)) && !precheckData} error={precheckError || (currentPrecheck && precheckQuery.error)}>
              {precheckData && <PrecheckResults data={precheckData} failed={precheckFailed} passed={precheckPassed}
                onRecheck={() => void triggerPrecheck(true)} recheckDisabled={startPrecheck.isPending || startRestore.isPending} />}
            </QueryState>
            {precheckError != null && (
              <Button variant="outline" size="sm" onClick={() => void triggerPrecheck()} disabled={startPrecheck.isPending || startRestore.isPending}>
                {precheckRetryable ? tx('Tekrar Dene') : tx('Yeniden Kontrol Et')}
              </Button>
            )}
          </Section>

          <Section title={tx("Başlatmadan önce")} description={tx("Onayınızdan sonra Agent hedef {0} üzerinde Restore çalıştırır.", { "0": engineName })}>
            <div className="space-y-4">
              <Facts>
                <Fact label={tx("Kaynak Database")}>{artifact.databaseName ?? tx('Bilinmeyen Database')}</Fact>
                <Fact label={tx("Hedef Database")}>{targetDatabaseName}</Fact>
                <Fact label={tx("Agent")}>{activeAgent?.hostName ?? selectedAgentId}</Fact>
                <Fact label={tx("Hedef {0}", { "0": engineName })}>{effectiveTarget.host}{effectiveTarget.port ? `:${effectiveTarget.port}` : ''}</Fact>
                <Fact label={tx("Restore modu")}>{restoreModeLabel(mode)}</Fact>
                <Fact label={tx("Ön kontrol")}>{precheckPassed ? tx('Geçti') : tx('Başarılı ön kontrol bekleniyor')}</Fact>
              </Facts>
              {mode === 'ReplaceExistingDatabase' && <Notice tone="warning" title={tx("Mevcut Database'in üzerine yazılacak")}>
                <p>{isPg ? tx('Mevcut Database bp_old_* adıyla korunur ve doğrulanmış geçici Database hedef adını alır. Eski Database otomatik silinmez; operatör kurtarma ve temizlikten sorumludur.') : <><strong className="break-all">{targetDatabaseName}</strong>{' '}{tx("içindeki eski veriler kalıcı olarak kaybolur. İşlem geri alınamaz; iptal etmek eski içeriği geri getirmez.")}</>}</p>
                <p className="mt-1">{tx("Database adı ile verdiğiniz onay:")}{' '}<code className="break-all">{confirmOverwriteInput}</code></p>
              </Notice>}
            </div>
          </Section>
          {executionError != null && (executionError instanceof ApiError ? <ErrorNotice error={executionError} scope="restore" /> : <ErrorNotice what={tx('Geri yükleme başlatılamadı.')} dataImpact={tx('Geri yüklemenin başlayıp başlamadığını geri yükleme listesinden kontrol edin.')} action={tx('Aynı işlemi tekrar deneyin; yeniden denemeler ikinci bir geri yükleme oluşturmaz.')} />)}

          <div className="flex flex-wrap justify-between gap-3">
            <Button variant="outline" onClick={() => setStep(2)}>{tx("← Mod Ayarlarına Dön")}</Button>
            <Button
              variant={mode === 'ReplaceExistingDatabase' ? 'destructive' : 'default'}
              disabled={!eligibleForRestore || !precheckPassed || precheckFailed || !replaceConfirmed || startPrecheck.isPending || startRestore.isPending}
              onClick={() => void triggerExecution()}
            >
              {startRestore.isPending ? tx('Geri Yükleme Başlatılıyor…') : executionRetryable ? tx('Tekrar Dene') : tx("{0} Olarak Restore Başlat", { "0": restoreModeLabel(mode) })}
            </Button>
          </div>
        </div>
      )}
    </div>
  )
}
