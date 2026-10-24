import { tx, useLanguage } from '@/lib/i18n'
import { useEffect, useRef, useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { Button } from '@/components/ui/button'
import { ErrorNotice } from '@/components/ui/notice'
import { Stepper } from '@/components/ui/stepper'
import { useTenant } from '@/features/tenancy/use-tenant'
import { getAgents } from '@/features/agents/api'
import { getDiscoveryInstance } from '@/features/discovery/api'
import { discoveredConfiguration } from '@/features/discovery/job-prefill'
import type { DiscoveryInstanceDetail } from '@/features/discovery/types'
import { useJobOccurrences, useSaveBackupJob } from '@/features/backup-jobs/hooks'
import { agentStep, jobSteps, nameStep, protectionStep, retentionStep, scheduleStep, sourceStep, stepOfField, storageStep, summaryStep, validateStep } from '@/features/backup-jobs/job-wizard-model'
import { NameStep } from '@/features/backup-jobs/steps/name-step'
import { AgentStep } from '@/features/backup-jobs/steps/agent-step'
import { SourceStep } from '@/features/backup-jobs/steps/source-step'
import { StorageStep } from '@/features/backup-jobs/steps/storage-step'
import { ScheduleStep } from '@/features/backup-jobs/steps/schedule-step'
import { RetentionStep } from '@/features/backup-jobs/steps/retention-step'
import { ProtectionStep } from '@/features/backup-jobs/steps/protection-step'
import { SummaryStep } from '@/features/backup-jobs/steps/summary-step'
import { useTenantConnections, useTenantProviders } from '@/features/storage-connections/hooks'
import { connectionUnavailableReason, primaryDestination, resolveDestinationConnection } from '@/features/storage-connections/job-connection-picker-utils'
import type { BackupJob, BackupJobInput, BackupConfiguration, Schedule, RetryPolicy, RetentionPolicy } from '@/features/backup-jobs/types'
import { ApiError } from '@/lib/api/client'
import { errorMessage, failureMessage } from '@/lib/api/failure-messages'
import { formatDateTime } from '@/lib/format'

const initialConfiguration: BackupConfiguration = { engine: 'mssql', database: '', host: '', port: null, integratedSecurity: true, databaseCredentialReference: null, encrypt: true, trustServerCertificate: false, stagingReference: 'default', storagePrefix: '', destinations: [{ storageProvider: 'local', storageConnectionId: null, storageExecutionLocation: 'Agent', isPrimary: true }], requiredVerifiedReplicaCount: 1, copyOnly: true, checksumPolicy: 'Required', compressionPolicy: 'Preferred', encryptionPolicy: 'Off' }
const initialSchedule: Schedule = { enabled: true, cronExpression: '0 2 * * *', timeZone: 'Europe/Istanbul', missedRunGraceSeconds: 3600, overlapPolicy: 'Skip' }
const initialRetry: RetryPolicy = { maxAttempts: 3, initialDelaySeconds: 30, maxDelaySeconds: 300, backoff: 'Exponential', jitter: 0.2 }
const initialRetention: RetentionPolicy = { enabled: true, keepForSeconds: 1209600, deleteGracePeriodSeconds: 86400 }

export function JobForm({ job, prefill }: { job?: BackupJob; prefill?: { instance: DiscoveryInstanceDetail; databases: string[] } }) {
  useLanguage()
  const { tenantId, canAdmin } = useTenant()
  const navigate = useNavigate()
  const save = useSaveBackupJob(job?.id)
  const occurrences = useJobOccurrences(job?.id ?? '')
  const agents = useQuery({ queryKey: ['tenant', tenantId, 'agents'], queryFn: ({ signal }) => getAgents(tenantId, signal) })
  const [name, setName] = useState(job?.name ?? '')
  const [agentId, setAgentId] = useState(job?.agentId ?? prefill?.instance.agentId ?? '')
  const [configuration, setConfiguration] = useState(() => job?.configuration.engine === 'postgres' ? { ...job.configuration, copyOnly: false } : job?.configuration ?? (prefill?.databases.length ? discoveredConfiguration(initialConfiguration, prefill.instance, prefill.databases) : initialConfiguration))
  // Databases picked from the discovery inventory; one job is created per name when the user confirms the summary.
  const [selected, setSelected] = useState<string[]>(prefill?.databases ?? [])
  const [results, setResults] = useState<Record<string, string>>({})
  const [discoveryInstanceId, setDiscoveryInstanceId] = useState(prefill?.instance.id ?? '')
  const [retry, setRetry] = useState(job?.retryPolicy ?? initialRetry)
  const [retention, setRetention] = useState(job?.retention ?? initialRetention)
  const [schedule, setSchedule] = useState(job?.schedule ?? initialSchedule)
  const [scheduled, setScheduled] = useState(job ? Boolean(job.schedule) : true)
  const [enabled, setEnabled] = useState(job?.enabled ?? true)
  const [overlap, setOverlap] = useState<'Skip' | 'Queue'>(job?.overlapPolicy ?? 'Skip')
  const [step, setStep] = useState(job ? summaryStep : 0)
  const [clientErrors, setClientErrors] = useState<Record<string, string>>({})
  const saving = useRef(false)
  const mounted = useRef(false)
  const formRef = useRef<HTMLFormElement>(null)
  const headingRef = useRef<HTMLHeadingElement>(null)
  const connections = useTenantConnections()
  const providers = useTenantProviders()
  const connectionItems = connections.data?.items ?? []
  const providerItems = providers.data?.items ?? []
  const primary = primaryDestination(configuration)
  const target = resolveDestinationConnection(primary, connectionItems)
  const targetProvider = providerItems.find(item => item.providerType === target?.providerType)
  const savedPrimary = job && primaryDestination(job.configuration)
  const existingTarget = Boolean(savedPrimary && (savedPrimary.storageConnectionId ?? null) === (primary.storageConnectionId ?? null) && savedPrimary.storageProvider === primary.storageProvider)
  const pickerPending = connections.isPending || providers.isPending
  const pickerError = connections.error ?? providers.error
  const targetBlocked = !existingTarget && Boolean(connectionUnavailableReason(target, targetProvider))
  const cannotSave = pickerPending || Boolean(pickerError) || targetBlocked
  function refreshConnections() {
    void connections.refetch()
    void providers.refetch()
  }
  const multi = selected.length > 1
  const saveError = multi ? null : save.error
  const serverErrors: Record<string, string> = saveError instanceof ApiError ? { ...Object.fromEntries(Object.entries(saveError.fields).map(([field, code]) => [field.replace(/^schedule\./, ''), failureMessage(code)])), ...(saveError.field ? { [saveError.field.replace(/^schedule\./, '')]: errorMessage(saveError) } : {}) } : {}
  const errors = { ...serverErrors, ...clientErrors }
  const errorSteps = [...new Set(Object.keys(serverErrors).map(stepOfField).filter((value): value is number => value !== undefined))].sort((a, b) => a - b)
  const nextOccurrence = job && (job.nextOccurrenceAt ?? job.schedule?.nextOccurrenceAt)
  const nextRun = job ? nextOccurrence ? formatDateTime(nextOccurrence) : tx('Planlanmamış') : undefined
  const agent = agents.data?.find(item => item.agentId === agentId)

  useEffect(() => {
    if (!mounted.current) { mounted.current = true; return }
    headingRef.current?.focus()
  }, [step])
  useEffect(() => {
    if (Object.keys(clientErrors).length) formRef.current?.querySelector<HTMLElement>('[aria-invalid="true"]')?.focus()
  }, [clientErrors])

  function go(target: number) {
    setClientErrors({})
    setStep(target)
  }
  function advance(target: number) {
    const found = validateStep(step, { name, agentId, configuration, scheduled, schedule, retry })
    if (Object.keys(found).length) setClientErrors(found)
    else go(target)
  }
  function submit() {
    if (saving.current || cannotSave) return
    saving.current = true
    const input: BackupJobInput = { name, agentId, configuration, retryPolicy: retry, overlapPolicy: overlap, enabled, schedule: scheduled ? { ...schedule, overlapPolicy: overlap } : null, ...(canAdmin ? { retention } : {}) }
    if (multi) { void createMany(input); return }
    save.mutate(input, {
      onSuccess: () => navigate(`/tenants/${tenantId}/backup-jobs`),
      onError: error => {
        if (error instanceof ApiError && ['storage_connection_disabled', 'storage_connection_unavailable'].includes(error.reason ?? error.code ?? '')) refreshConnections()
      },
      onSettled: () => { saving.current = false },
    })
  }
  async function createMany(input: BackupJobInput) {
    const outcome = { ...results }
    // A create that failed on the client side (timeout) may still have succeeded on the server: before retrying, look at the inventory's jobs.
    const covered = await coveredAfterFailure(selected.filter(item => outcome[item] && outcome[item] !== 'ok'))
    for (const database of selected.filter(item => outcome[item] !== 'ok')) {
      if (covered.has(database)) { outcome[database] = 'ok'; continue }
      try {
        await save.mutateAsync({ ...input, name: `${input.name} – ${database}`, configuration: { ...input.configuration, database } })
        outcome[database] = 'ok'
      } catch (error) { outcome[database] = errorMessage(error) }
      setResults({ ...outcome })
    }
    setResults({ ...outcome })
    saving.current = false
    if (selected.every(item => outcome[item] === 'ok')) navigate(`/tenants/${tenantId}/backup-jobs`)
  }
  async function coveredAfterFailure(retrying: string[]) {
    if (!discoveryInstanceId || retrying.length === 0) return new Set<string>()
    try {
      const detail = await getDiscoveryInstance(tenantId, discoveryInstanceId)
      return new Set(detail.databases.filter(database => retrying.includes(database.name) && database.jobs.length > 0).map(database => database.name))
    } catch { return new Set<string>() }
  }
  function applyDiscovery(instance: DiscoveryInstanceDetail, databases: string[]) {
    setAgentId(instance.agentId)
    setDiscoveryInstanceId(instance.id)
    setConfiguration(discoveredConfiguration(configuration, instance, databases))
    setSelected(databases)
    setResults({})
  }
  function clearDiscovery() {
    setSelected([])
    setResults({})
    setConfiguration({ ...configuration, database: '' })
  }
  const failedCount = Object.values(results).filter(value => value !== 'ok').length
  const last = step === summaryStep
  return <form ref={formRef} noValidate className="space-y-5" onSubmit={event => {
    event.preventDefault()
    if (last) submit()
    else advance(step + 1)
  }}>
    <Stepper steps={jobSteps.map(step => tx(step))} current={step} />
    <h2 ref={headingRef} tabIndex={-1} className="sr-only">{tx("Adım")}{' '}{step + 1} / {tx(jobSteps.length)}: {tx(jobSteps[step])}</h2>
    {step === nameStep && <NameStep name={name} onName={setName} enabled={enabled} onEnabled={setEnabled} errors={errors} />}
    {step === agentStep && <AgentStep agentId={agentId} onAgentId={setAgentId} agents={agents} errors={errors} />}
    {step === sourceStep && <SourceStep key={configuration.engine} configuration={configuration} errors={errors} discovery={job ? undefined : { agents: agents.data ?? [], agentId, selected, onApply: applyDiscovery, onClear: clearDiscovery }}
      onChange={next => { if (next.engine !== configuration.engine) { setSelected([]); setResults({}) } setConfiguration(next) }} />}
    {step === storageStep && <StorageStep configuration={configuration} errors={errors} onChange={setConfiguration} picker={{ connections: connectionItems, providers: providerItems, pending: pickerPending, error: pickerError, existingTarget, onRefresh: refreshConnections }} />}
    {step === scheduleStep && <ScheduleStep errors={errors} scheduled={scheduled} onScheduled={setScheduled} schedule={schedule} onSchedule={setSchedule} nextRun={nextRun} overlap={overlap} onOverlap={setOverlap} retry={retry} onRetry={setRetry} />}
    {step === retentionStep && <RetentionStep errors={errors} retention={retention} onRetention={setRetention} canAdmin={canAdmin} existing={Boolean(job)} />}
    {step === protectionStep && <ProtectionStep configuration={configuration} errors={errors} onChange={setConfiguration} />}
    {last && <SummaryStep selected={selected} results={results} occurrences={job ? occurrences : undefined} cannotSave={cannotSave} onFix={() => go(storageStep)}
      summary={{ name, agentLabel: agent ? `${agent.hostName ?? tx('Adsız bilgisayar')} · ${tx(agent.operationalStatus)}` : agentId ? tx('Bilgisayar bulunamadı') : tx('Seçilmedi'), enabled, configuration, scheduled, schedule, overlap, retry, retention, canAdmin, connections: connectionItems, nextRun, existing: Boolean(job), onGo: go }} />}
    {saveError && <ErrorNotice title={tx("İş kaydedilemedi")} error={saveError}
      control={errorSteps.length > 0 ? <div className="flex flex-wrap gap-2">{errorSteps.map(item => <Button key={item} type="button" variant="outline" size="sm" aria-label={tx("Hatalı alana git: {0}", { "0": tx(jobSteps[item]) })} onClick={() => go(item)}>{tx("Adıma git")}</Button>)}</div> : undefined} />}
    <div className="flex flex-wrap items-center justify-between gap-3">
      <Button type="button" variant="outline" disabled={step === nameStep || save.isPending} onClick={() => go(step - 1)}>{tx("Geri")}</Button>
      <div className="flex flex-wrap items-center gap-3">
        <Link className="text-sm text-primary underline-offset-4 hover:underline" to={`/tenants/${tenantId}/backup-jobs`}>{tx("Vazgeç")}</Link>
        {job && !last && <Button type="button" variant="outline" onClick={() => advance(summaryStep)}>{tx("Özete dön")}</Button>}
        {last ? <Button key="save" type="submit" size="lg" disabled={save.isPending || cannotSave}>{save.isPending ? tx('Kaydediliyor…') : failedCount > 0 ? tx('Başarısızları yeniden dene') : tx('Kaydet')}</Button> : <Button key="next" type="submit">{tx("İleri")}</Button>}
      </div>
    </div>
  </form>
}
