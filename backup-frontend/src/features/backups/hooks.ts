import { useEffect, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useTenant } from '@/features/tenancy/use-tenant'
import { cancelBackup, getBackup, getBackups, retryBackup } from './api'
import { isActiveRun } from './presentation'
import type { BackupRun, BackupRunFilters } from './types'

export const backupQueryKeys = {
  all: (tenantId: string) => ['tenant', tenantId, 'backup-runs'] as const,
  list: (tenantId: string, filters: BackupRunFilters) => ['tenant', tenantId, 'backup-runs', 'list', filters] as const,
  detail: (tenantId: string, id: string) => ['tenant', tenantId, 'backup-runs', 'detail', id] as const,
}

function useVisibleTab() {
  const [visible, setVisible] = useState(() => document.visibilityState === 'visible')
  useEffect(() => {
    const update = () => setVisible(document.visibilityState === 'visible')
    document.addEventListener('visibilitychange', update)
    return () => document.removeEventListener('visibilitychange', update)
  }, [])
  return visible
}

export function useBackups(filters: BackupRunFilters = {}) {
  const { tenantId } = useTenant()
  const visible = useVisibleTab()
  return useQuery({
    queryKey: backupQueryKeys.list(tenantId, filters),
    queryFn: ({ signal }) => getBackups(tenantId, filters, signal),
    refetchInterval: (query) => visible && !query.state.error && query.state.data?.items.some((run) => isActiveRun(run.status)) ? 5000 : false,
    refetchIntervalInBackground: false,
    refetchOnWindowFocus: false,
  })
}

export function useBackup(id: string) {
  const { tenantId } = useTenant()
  const visible = useVisibleTab()
  return useQuery({
    queryKey: backupQueryKeys.detail(tenantId, id),
    queryFn: ({ signal }) => getBackup(tenantId, id, signal),
    enabled: id.length > 0,
    refetchInterval: (query) => visible && !query.state.error && query.state.data && isActiveRun(query.state.data.status) ? 5000 : false,
    refetchIntervalInBackground: false,
    refetchOnWindowFocus: false,
  })
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
    ])
  }
  const retry = useMutation({ mutationFn: () => retryBackup(tenantId, id), onSuccess, retry: false })
  const cancel = useMutation({ mutationFn: () => cancelBackup(tenantId, id), onSuccess, retry: false })
  return { retry, cancel }
}
