import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useVisibleTab } from '@/hooks/use-visible-tab'
import { storageQueryKeys, useRefreshQuotaWhenFinished } from '@/features/storage/hooks'
import { useTenant } from '@/features/tenancy/use-tenant'
import { cancelBackup, getBackup, getBackups, pauseBackup, resumeBackup, retryBackup } from './api'
import { isActiveRun } from './presentation'
import type { BackupRun, BackupRunFilters } from './types'

export const backupQueryKeys = {
  all: (tenantId: string) => ['tenant', tenantId, 'backup-runs'] as const,
  list: (tenantId: string, filters: BackupRunFilters) => ['tenant', tenantId, 'backup-runs', 'list', filters] as const,
  detail: (tenantId: string, id: string) => ['tenant', tenantId, 'backup-runs', 'detail', id] as const,
}

export function useBackups(filters: BackupRunFilters = {}) {
  const { tenantId } = useTenant()
  const visible = useVisibleTab()
  const query = useQuery({
    queryKey: backupQueryKeys.list(tenantId, filters),
    queryFn: ({ signal }) => getBackups(tenantId, filters, signal),
    refetchInterval: (query) => visible && !query.state.error && query.state.data?.items.some((run) => isActiveRun(run.status)) ? 5000 : false,
    refetchIntervalInBackground: false,
    refetchOnWindowFocus: false,
  })
  // A run that finishes turns reserved capacity into used capacity (or releases it): the tenant's quota changed.
  useRefreshQuotaWhenFinished(tenantId, Boolean(query.data?.items.some((run) => isActiveRun(run.status))))
  return query
}

export function useBackup(id: string) {
  const { tenantId } = useTenant()
  const visible = useVisibleTab()
  const query = useQuery({
    queryKey: backupQueryKeys.detail(tenantId, id),
    queryFn: ({ signal }) => getBackup(tenantId, id, signal),
    enabled: id.length > 0,
    refetchInterval: (query) => visible && !query.state.error && query.state.data && isActiveRun(query.state.data.status) ? 5000 : false,
    refetchIntervalInBackground: false,
    refetchOnWindowFocus: false,
  })
  useRefreshQuotaWhenFinished(tenantId, Boolean(query.data && isActiveRun(query.data.status)))
  return query
}

export function useBackupActions(id: string) {
  const { tenantId } = useTenant()
  const client = useQueryClient()
  const onSuccess = async (run: BackupRun) => {
    // Cooperative cancellation is not a terminal state: the server response is authoritative.
    client.setQueryData(backupQueryKeys.detail(tenantId, id), run)
    await Promise.all([
      client.invalidateQueries({ queryKey: backupQueryKeys.all(tenantId) }),
      client.invalidateQueries({ queryKey: ['tenant', tenantId, 'dashboard'] }),
      client.invalidateQueries({ queryKey: ['tenant', tenantId, 'backup-artifacts'] }),
      client.invalidateQueries({ queryKey: storageQueryKeys.quota(tenantId) }),
    ])
  }
  const retry = useMutation({ mutationFn: () => retryBackup(tenantId, id), onSuccess, retry: false })
  const cancel = useMutation({ mutationFn: () => cancelBackup(tenantId, id), onSuccess, retry: false })
  const pause = useMutation({ mutationFn: () => pauseBackup(tenantId, id), onSuccess, retry: false })
  const resume = useMutation({ mutationFn: () => resumeBackup(tenantId, id), onSuccess, retry: false })
  return { retry, cancel, pause, resume }
}
