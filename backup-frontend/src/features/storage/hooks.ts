import { useEffect, useRef } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useVisibleTab } from '@/hooks/use-visible-tab'
import { useTenant } from '@/features/tenancy/use-tenant'
import { getTenantStorageQuota } from './api'

export const STORAGE_POLL_MS = 15_000

export const storageQueryKeys = {
  quota: (tenantId: string) => ['tenant', tenantId, 'storage-quota'] as const,
}

/**
 * The tenant's own quota standing. Never served from the 30 s default cache: it refetches on every mount and, while the tab is visible,
 * every 15 s, so a run that finished or a plan the platform changed shows up without a reload. A hidden tab makes no requests.
 */
export function useTenantStorageQuota() {
  const { tenantId } = useTenant()
  const visible = useVisibleTab()
  return useQuery({
    queryKey: storageQueryKeys.quota(tenantId),
    queryFn: ({ signal }) => getTenantStorageQuota(tenantId, signal),
    staleTime: 0,
    refetchOnMount: 'always',
    refetchInterval: (query) => visible && !query.state.error ? STORAGE_POLL_MS : false,
    refetchIntervalInBackground: false,
  })
}

/**
 * Calls `onSettled` when `active` goes from true to false (an in-flight backup run finished, a replica deletion was verified): the moments
 * quota changes without any request of this user's own. Not on first render.
 */
export function useOnFinished(active: boolean, onFinished: () => void) {
  const previous = useRef(active)
  const callback = useRef(onFinished)
  useEffect(() => { callback.current = onFinished })
  useEffect(() => {
    if (previous.current && !active) callback.current()
    previous.current = active
  }, [active])
}

/** Invalidates the tenant's quota (and the dashboard that sums artifacts) when `active` work has just finished. */
export function useRefreshQuotaWhenFinished(tenantId: string, active: boolean) {
  const client = useQueryClient()
  useOnFinished(active, () => {
    void client.invalidateQueries({ queryKey: storageQueryKeys.quota(tenantId) })
    void client.invalidateQueries({ queryKey: ['tenant', tenantId, 'dashboard'] })
  })
}
