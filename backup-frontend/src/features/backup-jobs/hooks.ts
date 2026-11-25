import { useRef } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useTenant } from '@/features/tenancy/use-tenant'
import { ApiError } from '@/lib/api/client'
import { isUncertainError } from '@/lib/idempotency-key'
import { getBackupJob, getBackupJobs, getOccurrences, saveBackupJob, setJobEnabled, startJobRun } from './api'
import { isActiveRun } from '@/features/backups/presentation'
import type { BackupJob, BackupJobInput } from './types'

export const backupJobQueryKeys = {
  all: (tenantId: string) => ['tenant', tenantId, 'backup-jobs'] as const,
  detail: (tenantId: string, id: string) => ['tenant', tenantId, 'backup-jobs', id] as const,
}
export function useBackupJobs({ includeArchived = false }: { includeArchived?: boolean } = {}) {
  const { tenantId } = useTenant()
  // While a run is active the list is re-read, so a finished run shows its result without a reload.
  const whileRunning = (query: { state: { data?: BackupJob[] } }) => query.state.data?.some(job => job.lastRun && isActiveRun(job.lastRun.status)) ? 5000 : false
  return useQuery({ queryKey: includeArchived ? [...backupJobQueryKeys.all(tenantId), 'with-archived'] : backupJobQueryKeys.all(tenantId), queryFn: ({ signal }) => getBackupJobs(tenantId, signal, includeArchived), refetchInterval: whileRunning })
}
export function useBackupJob(id: string) {
  const { tenantId } = useTenant()
  return useQuery({ queryKey: backupJobQueryKeys.detail(tenantId, id), queryFn: ({ signal }) => getBackupJob(tenantId, id, signal), enabled: Boolean(id) })
}
export function useJobOccurrences(id: string) {
  const { tenantId } = useTenant()
  return useQuery({ queryKey: [...backupJobQueryKeys.detail(tenantId, id), 'occurrences'], queryFn: ({ signal }) => getOccurrences(tenantId, id, signal), enabled: Boolean(id) })
}
function useInvalidateJobs() {
  const { tenantId } = useTenant()
  const client = useQueryClient()
  return () => Promise.all(['backup-jobs', 'backup-runs', 'dashboard'].map(resource => client.invalidateQueries({ queryKey: ['tenant', tenantId, resource] })))
}
export function useSaveBackupJob(id?: string) {
  const { tenantId } = useTenant()
  const invalidate = useInvalidateJobs()
  const actions = useRef(new Map<string, { key: string; pending: number; settled: boolean }>())
  return useMutation({ mutationFn: async (input: BackupJobInput) => {
    if (id) return saveBackupJob(tenantId, input, id)
    const body = JSON.stringify([tenantId, input])
    const action = actions.current.get(body) ?? { key: crypto.randomUUID(), pending: 0, settled: false }
    actions.current.set(body, action)
    action.pending++
    try {
      const result = await saveBackupJob(tenantId, input, undefined, action.key)
      action.settled = true
      return result
    } catch (error) {
      if (!isUncertainError(error)) action.settled = true
      throw error
    } finally {
      if (--action.pending === 0 && action.settled) actions.current.delete(body)
    }
  }, onSuccess: invalidate, onError: invalidate })
}
/** Saves the job's own configuration with the server certificate trusted, then runs it again: the trust stays on the job. */
export function useTrustJobCertificate() {
  const { tenantId } = useTenant()
  const invalidate = useInvalidateJobs()
  return useMutation({ mutationFn: async (id: string) => {
    const { name, agentId, configuration, retryPolicy, overlapPolicy, schedule, retention } = await getBackupJob(tenantId, id)
    await saveBackupJob(tenantId, { name, agentId, configuration: { ...configuration, trustServerCertificate: true }, retryPolicy, overlapPolicy, schedule, retention }, id)
    return startJobRun(tenantId, id, crypto.randomUUID())
  }, retry: false, onSettled: invalidate })
}
export function useToggleBackupJob() {
  const { tenantId } = useTenant()
  const invalidate = useInvalidateJobs()
  return useMutation({ mutationFn: ({ id, enabled }: { id: string; enabled: boolean }) => {
    return setJobEnabled(tenantId, id, enabled)
  }, onSuccess: invalidate })
}
export function useRunBackupJob() {
  const { tenantId } = useTenant()
  const invalidate = useInvalidateJobs()
  const actions = useRef(new Map<string, { key: string; pending: boolean }>())
  const mutation = useMutation({ mutationFn: ({ id, key }: { id: string; key: string }) => startJobRun(tenantId, id, key), retry: false, onSuccess: invalidate })
  const run = async (id: string) => {
    const actionId = `${tenantId}/${id}`
    const previous = actions.current.get(actionId)
    if (previous?.pending) return undefined
    const action = previous ?? { key: crypto.randomUUID(), pending: false }
    action.pending = true
    actions.current.set(actionId, action)
    try {
      const result = await mutation.mutateAsync({ id, key: action.key })
      actions.current.delete(actionId)
      return result
    } catch (error) {
      if (error instanceof ApiError && error.status >= 400 && error.status < 500) actions.current.delete(actionId)
      throw error
    } finally { action.pending = false }
  }
  return { ...mutation, run }
}
