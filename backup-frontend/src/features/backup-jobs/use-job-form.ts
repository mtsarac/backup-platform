import { tx } from '@/lib/i18n'
import { useEffect, useRef, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { useTenant } from '@/features/tenancy/use-tenant'
import { getAgents } from '@/features/agents/api'
import { discoveredConfiguration } from '@/features/discovery/job-prefill'
import type { DiscoveryInstanceDetail } from '@/features/discovery/types'
import { useSaveBackupJob } from '@/features/backup-jobs/hooks'
import type { FieldErrors } from '@/features/backup-jobs/job-field-kit'
import { initialValues, resetSource, switchEngine, type Engine, type EngineMemory, type JobDraft, type JobPrefill, type JobValues } from '@/features/backup-jobs/job-form-values'
import { stepOfField, validateJob } from '@/features/backup-jobs/job-wizard-model'
import { useTenantConnections, useTenantProviders } from '@/features/storage-connections/hooks'
import { connectionUnavailableReason, primaryDestination, resolveDestinationConnection } from '@/features/storage-connections/job-connection-picker-utils'
import type { BackupConfiguration, BackupJob, BackupJobInput } from '@/features/backup-jobs/types'
import { ApiError } from '@/lib/api/client'
import { isUncertainError } from '@/lib/idempotency-key'
import { errorMessage, failureMessage } from '@/lib/api/failure-messages'
import { formatDateTime } from '@/lib/format'

/** State, validation inputs and saving shared by the create wizard and the section forms of an existing job. `onSaved` replaces the return to the list. */
export function useJobForm({ job, prefill, draft, storageConnectionId, onSaved }: { job?: BackupJob; prefill?: JobPrefill; draft?: JobDraft; storageConnectionId?: string; onSaved?: () => void }) {
  const { tenantId } = useTenant()
  const navigate = useNavigate()
  const save = useSaveBackupJob(job?.id)
  const agents = useQuery({ queryKey: ['tenant', tenantId, 'agents'], queryFn: ({ signal }) => getAgents(tenantId, signal) })
  const [saved, setSaved] = useState(() => initialValues(job, prefill))
  const [values, setValues] = useState(draft?.values ?? saved)
  const [results, setResults] = useState<Record<string, string>>({})
  /** The inventory instance the selected databases belong to. */
  const [discovered, setDiscovered] = useState<DiscoveryInstanceDetail | null>(draft ? draft.discovered : prefill?.databases.length ? prefill.instance : null)
  const [clientErrors, setClientErrors] = useState<FieldErrors>({})
  const engines = useRef<EngineMemory>({})
  const saving = useRef(false)
  const uncertain = useRef(new Map<string, BackupJobInput>())
  const [finished, setFinished] = useState(false)
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
  // A message shown by Devam/Kaydet goes away as soon as its field is fixed, without pressing the button again.
  const stillInvalid = Object.keys(clientErrors).length ? validateJob(values).errors : {}
  const nextOccurrence = job && (job.nextOccurrenceAt ?? job.schedule?.nextOccurrenceAt)

  function refreshConnections() {
    void connections.refetch()
    void providers.refetch()
  }
  const set = (patch: Partial<JobValues>) => {
    setFinished(false)
    setValues(current => ({ ...current, ...patch }))
  }
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
    setDiscovered(instance)
    set({ agentId: instance.agentId, configuration: discoveredConfiguration(configuration, instance, databases), selected: databases })
    setResults({})
  }
  function clearDiscovery() {
    setDiscovered(null)
    set({ selected: [], configuration: resetSource(configuration) })
    setResults({})
  }
  // A connection just created in the storage wizard becomes the primary target once the refreshed lists know it.
  const adopted = useRef(false)
  const adoptable = storageConnectionId ? connectionItems.find(item => item.id === storageConnectionId) : undefined
  const adoptableLocation = providerItems.find(item => item.providerType === adoptable?.providerType)?.executionLocation
  useEffect(() => {
    if (adopted.current || !adoptable || !adoptableLocation) return
    adopted.current = true
    setValues(current => ({ ...current, configuration: { ...current.configuration, destinations: current.configuration.destinations.map(item => item.isPrimary ? { ...item, storageProvider: adoptable.providerType, storageConnectionId: adoptable.id, storageExecutionLocation: adoptableLocation } : item) } }))
  }, [adoptable, adoptableLocation])
  const listPath = `/tenants/${tenantId}/backup-jobs`
  useEffect(() => {
    if (!finished) return
    void navigate(listPath)
  }, [finished, listPath, navigate])
  function finish() {
    setSaved(values)
    if (onSaved) onSaved()
    else setFinished(true)
  }
  /** Starts a section edit from the job as it is now, or drops the edit in progress (`next` omitted). */
  function restart(next?: BackupJob) {
    const fresh = next ? initialValues(next) : saved
    setSaved(fresh)
    setValues(fresh)
    setClientErrors({})
    save.reset()
  }
  function submit() {
    if (saving.current || cannotSave) return
    saving.current = true
    const { name, agentId, enabled, scheduled, schedule, overlap, retry, retention } = values
    const input: BackupJobInput = { name, agentId, configuration, retryPolicy: retry, overlapPolicy: overlap, ...(!job ? { enabled } : {}), schedule: scheduled ? { ...schedule, overlapPolicy: overlap } : null, retention }
    if (multi) { void createMany(input); return }
    save.mutate(input, {
      onSuccess: finish,
      onError: error => {
        if (error instanceof ApiError && ['storage_connection_disabled', 'storage_connection_unavailable'].includes(error.reason ?? error.code ?? '')) refreshConnections()
      },
      onSettled: () => { saving.current = false },
    })
  }
  async function createMany(input: BackupJobInput) {
    const outcome = { ...results }
    for (const database of selected.filter(item => outcome[item] !== 'ok')) {
      // The server may have created it: the retry must carry the same body to reuse the same key
      const request = uncertain.current.get(database) ?? { ...input, name: `${input.name} – ${database}`, configuration: { ...input.configuration, database } }
      try {
        await save.mutateAsync(request)
        uncertain.current.delete(database)
        outcome[database] = 'ok'
      } catch (error) {
        if (isUncertainError(error)) uncertain.current.set(database, request)
        else uncertain.current.delete(database)
        outcome[database] = errorMessage(error)
      }
      setResults({ ...outcome })
    }
    setResults({ ...outcome })
    saving.current = false
    if (selected.every(item => outcome[item] === 'ok')) finish()
  }

  useEffect(() => {
    if (!Object.keys(clientErrors).length) return
    document.querySelector<HTMLElement>('form [aria-invalid="true"]')?.focus()
  }, [clientErrors])

  return {
    job, tenantId, listPath, saved, values, restart, set, setConfiguration, changeEngine, applyDiscovery, clearDiscovery, discovered, prefillInstanceId: prefill?.instance.id,
    agents, agent: agents.data?.find(item => item.agentId === values.agentId),
    picker: { connections: connectionItems, providers: providerItems, pending: pickerPending, error: pickerError, existingTarget, onRefresh: refreshConnections },
    cannotSave, results, failedCount: Object.values(results).filter(value => value !== 'ok').length,
    save, saveError, serverErrors, clientErrors, setClientErrors, errors: { ...serverErrors, ...Object.fromEntries(Object.entries(clientErrors).filter(([field]) => field in stillInvalid)) } as FieldErrors,
    errorSteps: [...new Set(Object.keys(serverErrors).map(stepOfField).filter(step => step !== undefined))],
    dirty: JSON.stringify(values) !== JSON.stringify(saved), submit,
    nextRun: job ? nextOccurrence ? formatDateTime(nextOccurrence) : tx('Planlanmamış') : undefined,
  }
}
export type JobFormState = ReturnType<typeof useJobForm>
