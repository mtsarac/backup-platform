import { tx } from '@/lib/i18n'
import { useEffect, useRef, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { useTenant } from '@/features/tenancy/use-tenant'
import { getAgents } from '@/features/agents/api'
import { getDiscoveryInstance } from '@/features/discovery/api'
import { discoveredConfiguration } from '@/features/discovery/job-prefill'
import type { DiscoveryInstanceDetail } from '@/features/discovery/types'
import { useSaveBackupJob } from '@/features/backup-jobs/hooks'
import type { FieldErrors } from '@/features/backup-jobs/job-field-kit'
import { initialValues, switchEngine, type Engine, type EngineMemory, type JobPrefill, type JobValues } from '@/features/backup-jobs/job-form-values'
import { stepOfField } from '@/features/backup-jobs/job-wizard-model'
import { useTenantConnections, useTenantProviders } from '@/features/storage-connections/hooks'
import { connectionUnavailableReason, primaryDestination, resolveDestinationConnection } from '@/features/storage-connections/job-connection-picker-utils'
import type { BackupConfiguration, BackupJob, BackupJobInput } from '@/features/backup-jobs/types'
import { ApiError } from '@/lib/api/client'
import { errorMessage, failureMessage } from '@/lib/api/failure-messages'
import { formatDateTime } from '@/lib/format'

/** State, validation inputs and saving shared by the create wizard and the single-page edit form. */
export function useJobForm({ job, prefill }: { job?: BackupJob; prefill?: JobPrefill }) {
  const { tenantId, canAdmin } = useTenant()
  const navigate = useNavigate()
  const save = useSaveBackupJob(job?.id)
  const agents = useQuery({ queryKey: ['tenant', tenantId, 'agents'], queryFn: ({ signal }) => getAgents(tenantId, signal) })
  const [saved] = useState(() => initialValues(job, prefill))
  const [values, setValues] = useState(saved)
  const [results, setResults] = useState<Record<string, string>>({})
  const [discoveryInstanceId, setDiscoveryInstanceId] = useState(prefill?.instance.id ?? '')
  const [clientErrors, setClientErrors] = useState<FieldErrors>({})
  const engines = useRef<EngineMemory>({})
  const saving = useRef(false)
  const connections = useTenantConnections()
  const providers = useTenantProviders()
  const connectionItems = connections.data?.items ?? []
  const providerItems = providers.data?.items ?? []
  const { configuration, selected } = values
  const primary = primaryDestination(configuration)
  const target = resolveDestinationConnection(primary, connectionItems)
  const targetProvider = providerItems.find(item => item.providerType === target?.providerType)
  const savedPrimary = job && primaryDestination(job.configuration)
  const existingTarget = Boolean(savedPrimary && (savedPrimary.storageConnectionId ?? null) === (primary.storageConnectionId ?? null) && savedPrimary.storageProvider === primary.storageProvider)
  const pickerPending = connections.isPending || providers.isPending
  const pickerError = connections.error ?? providers.error
  const targetBlocked = !existingTarget && Boolean(connectionUnavailableReason(target, targetProvider))
  const cannotSave = pickerPending || Boolean(pickerError) || targetBlocked
  const multi = selected.length > 1
  const saveError = multi ? null : save.error
  const serverErrors: FieldErrors = saveError instanceof ApiError ? { ...Object.fromEntries(Object.entries(saveError.fields).map(([field, code]) => [field.replace(/^schedule\./, ''), failureMessage(code)])), ...(saveError.field ? { [saveError.field.replace(/^schedule\./, '')]: errorMessage(saveError) } : {}) } : {}
  const nextOccurrence = job && (job.nextOccurrenceAt ?? job.schedule?.nextOccurrenceAt)

  function refreshConnections() {
    void connections.refetch()
    void providers.refetch()
  }
  const set = (patch: Partial<JobValues>) => setValues(current => ({ ...current, ...patch }))
  const setConfiguration = (next: BackupConfiguration) => set({ configuration: next })
  function changeEngine(engine: Engine) {
    if (engine === configuration.engine) return
    const next = switchEngine(configuration, engines.current, engine)
    engines.current = next.memory
    // A discovery pick belongs to one engine's inventory.
    set({ configuration: next.configuration, selected: [] })
    setResults({})
  }
  function applyDiscovery(instance: DiscoveryInstanceDetail, databases: string[]) {
    setDiscoveryInstanceId(instance.id)
    set({ agentId: instance.agentId, configuration: discoveredConfiguration(configuration, instance, databases), selected: databases })
    setResults({})
  }
  function clearDiscovery() {
    set({ selected: [], configuration: { ...configuration, database: '' } })
    setResults({})
  }
  const listPath = `/tenants/${tenantId}/backup-jobs`
  function submit() {
    if (saving.current || cannotSave) return
    saving.current = true
    const { name, agentId, enabled, scheduled, schedule, overlap, retry, retention } = values
    const input: BackupJobInput = { name, agentId, configuration, retryPolicy: retry, overlapPolicy: overlap, enabled, schedule: scheduled ? { ...schedule, overlapPolicy: overlap } : null, ...(canAdmin ? { retention } : {}) }
    if (multi) { void createMany(input); return }
    save.mutate(input, {
      onSuccess: () => navigate(listPath),
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
    if (selected.every(item => outcome[item] === 'ok')) navigate(listPath)
  }
  async function coveredAfterFailure(retrying: string[]) {
    if (!discoveryInstanceId || retrying.length === 0) return new Set<string>()
    try {
      const detail = await getDiscoveryInstance(tenantId, discoveryInstanceId)
      return new Set(detail.databases.filter(database => retrying.includes(database.name) && database.jobs.length > 0).map(database => database.name))
    } catch { return new Set<string>() }
  }

  useEffect(() => {
    if (!Object.keys(clientErrors).length) return
    document.querySelector<HTMLElement>('form [aria-invalid="true"]')?.focus()
  }, [clientErrors])

  return {
    job, tenantId, canAdmin, listPath, saved, values, set, setConfiguration, changeEngine, applyDiscovery, clearDiscovery,
    agents, agent: agents.data?.find(item => item.agentId === values.agentId),
    picker: { connections: connectionItems, providers: providerItems, pending: pickerPending, error: pickerError, existingTarget, onRefresh: refreshConnections },
    cannotSave, results, failedCount: Object.values(results).filter(value => value !== 'ok').length,
    save, saveError, serverErrors, clientErrors, setClientErrors, errors: { ...serverErrors, ...clientErrors } as FieldErrors,
    errorSteps: [...new Set(Object.keys(serverErrors).map(stepOfField).filter(step => step !== undefined))],
    dirty: JSON.stringify(values) !== JSON.stringify(saved), submit,
    nextRun: job ? nextOccurrence ? formatDateTime(nextOccurrence) : tx('Planlanmamış') : undefined,
  }
}
export type JobFormState = ReturnType<typeof useJobForm>
