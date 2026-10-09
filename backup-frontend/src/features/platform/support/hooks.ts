import { useEffect, useRef } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import type { QueryClient } from '@tanstack/react-query'
import { useVisibleTab } from '@/hooks/use-visible-tab'
import { isActiveRestore } from '@/features/restores/presentation'
import type { PageParams } from '@/features/platform/types'
import type { RestoreView } from '@/features/restores/types'
import * as api from './api'
import type { OpenSupportSessionInput, SupportDataFilters, SupportPrecheckInput, SupportRestoreInput, SupportScope, SupportSession, SupportSessionFilters } from './types'

export const supportKeys = {
  lists: ['platform', 'support', 'sessions'] as const,
  list: (filters: SupportSessionFilters) => ['platform', 'support', 'sessions', filters] as const,
  session: (id: string) => ['platform', 'support', 'session', id, 'view'] as const,
  scope: (sessionId: string, tenantId: string) => ['platform', 'support', 'session', sessionId, 'tenant', tenantId] as const,
  data: (scope: SupportScope, resource: string, ...params: unknown[]) => ['platform', 'support', 'session', scope.sessionId, 'tenant', scope.tenantId, resource, ...params] as const,
}
/** Cancel before removing so late customer-data responses cannot repopulate an ended session. Session view and audit are retained. */
export function removeSupportData(client: QueryClient, sessionId: string) {
  const filters = { queryKey: ['platform', 'support', 'session', sessionId], predicate: (query: { queryKey: readonly unknown[] }) => query.queryKey[4] === 'tenant' && query.queryKey[6] !== 'audit' }
  void client.cancelQueries(filters)
  client.removeQueries(filters)
}
export function useSupportSessions(filters: SupportSessionFilters) {
  return useQuery({ queryKey: supportKeys.list(filters), queryFn: ({ signal }) => api.getSessions(filters, signal) })
}
export function useSupportSession(id: string) {
  const visible = useVisibleTab()
  const client = useQueryClient()
  const result = useQuery({ queryKey: supportKeys.session(id), queryFn: ({ signal }) => api.getSession(id, signal), enabled: Boolean(id), refetchInterval: query => visible && !query.state.error && query.state.data?.status === 'Active' ? 30_000 : false })
  useEffect(() => { if (result.data && result.data.status !== 'Active') removeSupportData(client, id) }, [client, id, result.data])
  return result
}
function useSupportControllerMounted() {
  const mounted = useRef(true)
  useEffect(() => { mounted.current = true; return () => { mounted.current = false } }, [])
  return mounted
}

export function useOpenSupportSession() {
  const client = useQueryClient()
  const mounted = useSupportControllerMounted()
  return useMutation({ mutationFn: ({ input, idempotencyKey }: { input: OpenSupportSessionInput; idempotencyKey: string }) => api.openSession(input, idempotencyKey), onSuccess: session => { if (!mounted.current) return; client.setQueryData(supportKeys.session(session.id), session); void client.invalidateQueries({ queryKey: supportKeys.lists }) } })
}
export function useEndSupportSession(id: string) {
  const client = useQueryClient()
  const mounted = useSupportControllerMounted()
  function updated(session: SupportSession) {
    if (!mounted.current || !client.getQueryData(supportKeys.session(id))) return
    removeSupportData(client, id)
    client.setQueryData(supportKeys.session(id), session)
    void client.invalidateQueries({ queryKey: supportKeys.lists })
    void client.invalidateQueries({ queryKey: ['platform', 'support', 'session', id], predicate: query => query.queryKey[6] === 'audit' })
  }
  const close = useMutation({ mutationKey: [...supportKeys.session(id), 'close'], mutationFn: () => api.closeSession(id), onSuccess: updated, retry: false })
  const revoke = useMutation({ mutationKey: [...supportKeys.session(id), 'revoke'], mutationFn: (reason: string) => api.revokeSession(id, reason), onSuccess: updated, retry: false })
  return { close, revoke }
}
export function useSupportAudit(session: SupportSession, params: PageParams) {
  return useQuery({ queryKey: supportKeys.data({ sessionId: session.id, tenantId: session.tenantId, enabled: true }, 'audit', params), queryFn: ({ signal }) => api.getSessionAudit(session.id, params, signal) })
}
export function useSupportJobs(scope: SupportScope) {
  return useQuery({ queryKey: supportKeys.data(scope, 'jobs'), queryFn: ({ signal }) => api.getSupportJobs(scope.sessionId, signal), enabled: scope.enabled })
}
export function useSupportRuns(scope: SupportScope, filters: SupportDataFilters) {
  return useQuery({ queryKey: supportKeys.data(scope, 'runs', filters), queryFn: ({ signal }) => api.getSupportRuns(scope.sessionId, filters, signal), enabled: scope.enabled })
}
export function useSupportArtifacts(scope: SupportScope, filters: SupportDataFilters) {
  return useQuery({ queryKey: supportKeys.data(scope, 'artifacts', 'list', filters), queryFn: ({ signal }) => api.getSupportArtifacts(scope.sessionId, filters, signal), enabled: scope.enabled })
}
export function useSupportArtifact(scope: SupportScope, artifactId: string) {
  return useQuery({ queryKey: supportKeys.data(scope, 'artifacts', artifactId), queryFn: ({ signal }) => api.getSupportArtifact(scope.sessionId, artifactId, signal), enabled: scope.enabled && Boolean(artifactId) })
}
export function useSupportDownloads(scope: SupportScope, params: PageParams) {
  const visible = useVisibleTab()
  return useQuery({ queryKey: supportKeys.data(scope, 'downloads', params), queryFn: ({ signal }) => api.getSupportDownloads(scope.sessionId, params, signal), enabled: scope.enabled, refetchInterval: query => visible && !query.state.error && query.state.data?.items.some(item => ['Preparing', 'Ready', 'Streaming'].includes(item.state)) ? 5000 : false })
}
export function useSupportRestoreTargets(scope: SupportScope, artifactId: string) {
  return useQuery({ queryKey: supportKeys.data(scope, 'restores', 'targets', artifactId), queryFn: ({ signal }) => api.getSupportRestoreTargets(scope.sessionId, artifactId, signal), enabled: scope.enabled && Boolean(artifactId) })
}
export function useSupportRestores(scope: SupportScope, params: SupportDataFilters) {
  const visible = useVisibleTab()
  return useQuery({ queryKey: supportKeys.data(scope, 'restores', 'list', params), queryFn: ({ signal }) => api.getSupportRestores(scope.sessionId, params, signal), enabled: scope.enabled, refetchInterval: query => visible && !query.state.error && query.state.data?.items.some(item => isActiveRestore(item.status)) ? 5000 : false })
}
export function useSupportRestore(scope: SupportScope, id: string) {
  const visible = useVisibleTab()
  return useQuery({ queryKey: supportKeys.data(scope, 'restores', 'detail', id), queryFn: ({ signal }) => api.getSupportRestore(scope.sessionId, id, signal), enabled: scope.enabled && Boolean(id), refetchInterval: query => visible && !query.state.error && query.state.data && isActiveRestore(query.state.data.status) ? 2000 : false })
}
export function useSupportRestoreActions(scope: SupportScope, artifactId: string) {
  const client = useQueryClient()
  const mounted = useSupportControllerMounted()
  function updated(restore: RestoreView) { if (mounted.current && scope.enabled && client.getQueryData<SupportSession>(supportKeys.session(scope.sessionId))?.status === 'Active') client.setQueryData(supportKeys.data(scope, 'restores', 'detail', restore.id), restore) }
  const precheck = useMutation({ mutationKey: supportKeys.data(scope, 'restores', 'precheck', artifactId), mutationFn: ({ input, idempotencyKey }: { input: SupportPrecheckInput; idempotencyKey: string }) => api.precheckSupportRestore(scope.sessionId, artifactId, input, idempotencyKey), retry: false, onSuccess: updated })
  const start = useMutation({ mutationKey: supportKeys.data(scope, 'restores', 'start', artifactId), mutationFn: ({ input, idempotencyKey }: { input: SupportRestoreInput; idempotencyKey: string }) => api.startSupportRestore(scope.sessionId, artifactId, input, idempotencyKey), retry: false, onSuccess: restore => { updated(restore); void client.invalidateQueries({ queryKey: supportKeys.data(scope, 'restores', 'list') }) } })
  return { precheck, start }
}
export function useCancelSupportRestore(scope: SupportScope, id: string) {
  const client = useQueryClient()
  const mounted = useSupportControllerMounted()
  return useMutation({ mutationKey: supportKeys.data(scope, 'restores', 'cancel', id), mutationFn: () => api.cancelSupportRestore(scope.sessionId, id), retry: false, onSuccess: restore => { if (!mounted.current || client.getQueryData<SupportSession>(supportKeys.session(scope.sessionId))?.status !== 'Active') return; client.setQueryData(supportKeys.data(scope, 'restores', 'detail', id), restore); void client.invalidateQueries({ queryKey: supportKeys.data(scope, 'restores', 'list') }) } })
}
