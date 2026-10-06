import { useRef } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useTenant } from '@/features/tenancy/use-tenant'
import { ApiError } from '@/lib/api/client'
import { getBackupJob, getBackupJobs, getOccurrences, saveBackupJob, setJobEnabled, startJobRun } from './api'
import type { BackupJobInput } from './types'

export const backupJobQueryKeys = {
  all: (tenantId: string) => ['tenant', tenantId, 'backup-jobs'] as const,
  detail: (tenantId: string, id: string) => ['tenant', tenantId, 'backup-jobs', id] as const,
}
export function useBackupJobs() {
  const { tenantId } = useTenant()
  return useQuery({ queryKey: backupJobQueryKeys.all(tenantId), queryFn: ({ signal }) => getBackupJobs(tenantId, signal) })
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
  const { tenantId, canOperate, canAdmin } = useTenant()
  const invalidate = useInvalidateJobs()
  return useMutation({ mutationFn: (input: BackupJobInput) => {
    if (!canOperate) throw new Error('Bu işlem için yetkiniz yok.')
    return saveBackupJob(tenantId, { ...input, retention: canAdmin ? input.retention : undefined }, id)
  }, onSuccess: invalidate, onError: invalidate })
}
export function useToggleBackupJob() {
  const { tenantId, canOperate } = useTenant()
  const invalidate = useInvalidateJobs()
  return useMutation({ mutationFn: ({ id, enabled }: { id: string; enabled: boolean }) => {
    if (!canOperate) throw new Error('Bu işlem için yetkiniz yok.')
    return setJobEnabled(tenantId, id, enabled)
  }, onSuccess: invalidate })
}
export function useRunBackupJob() {
  const { tenantId, canOperate } = useTenant()
  const invalidate = useInvalidateJobs()
  const actions = useRef(new Map<string, { key: string; pending: boolean }>())
  const mutation = useMutation({ mutationFn: ({ id, key }: { id: string; key: string }) => startJobRun(tenantId, id, key), retry: false, onSuccess: invalidate })
  const run = async (id: string) => {
    if (!canOperate) throw new Error('Bu işlem için yetkiniz yok.')
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
