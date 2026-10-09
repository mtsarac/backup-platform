import { keepPreviousData, useMutation, useQuery, useQueryClient, type QueryClient, type UseMutationResult } from '@tanstack/react-query'
import { platformKeys } from '@/features/platform/hooks'
import { storageQueryKeys } from '@/features/storage/hooks'
import { useTenant } from '@/features/tenancy/use-tenant'
import {
  clearPlatformConnectionSecret, clearTenantConnectionSecret, createPlatformConnection, createTenantConnection, deletePlatformConnection,
  deleteTenantConnection, getPlatformConnections, getPlatformProviders, getPlatformTenantConnections, getTenantConnections, getTenantProviders,
  setPlatformConnectionEnabled, setPlatformConnectionSecret, setTenantConnectionEnabled, setTenantConnectionSecret, testPlatformConnection,
  testTenantConnection, updatePlatformConnection, updateTenantConnection,
} from './api'
import type {
  ConnectionTestResponse, ConnectionView, PlatformConnectionFilters, PlatformCreateConnectionInput, PlatformUpdateConnectionInput,
  SecretValues, TenantCreateConnectionInput,
} from './types'

/** Secret VALUES are request bodies only: they are never part of a key. */
export const storageConnectionKeys = {
  tenantAll: (tenantId: string) => ['tenant', tenantId, 'storage-connections'] as const,
  tenantList: (tenantId: string) => ['tenant', tenantId, 'storage-connections', 'list'] as const,
  tenantProviders: (tenantId: string) => ['tenant', tenantId, 'storage-connections', 'providers'] as const,
  platformAll: ['platform', 'storage-connections'] as const,
  platformList: (filters: PlatformConnectionFilters) => ['platform', 'storage-connections', 'list', filters] as const,
  platformLists: ['platform', 'storage-connections', 'list'] as const,
  platformTenantList: (tenantId: string) => ['platform', 'storage-connections', 'tenant', tenantId] as const,
  platformTenantLists: ['platform', 'storage-connections', 'tenant'] as const,
  platformProviders: ['platform', 'storage-connections', 'providers'] as const,
}

/** A connection change moves enablement, quota scope or replica accounting, so the tenant's quota and dashboard refresh with the list. */
function invalidateTenantViews(client: QueryClient, tenantId: string) {
  return Promise.all([
    client.invalidateQueries({ queryKey: storageConnectionKeys.tenantList(tenantId) }),
    client.invalidateQueries({ queryKey: storageQueryKeys.quota(tenantId) }),
    client.invalidateQueries({ queryKey: ['tenant', tenantId, 'dashboard'] }),
  ])
}

function invalidatePlatformViews(client: QueryClient, tenantId: string) {
  return Promise.all([
    client.invalidateQueries({ queryKey: storageConnectionKeys.platformLists }),
    client.invalidateQueries({ queryKey: storageConnectionKeys.platformTenantLists }),
    client.invalidateQueries({ queryKey: platformKeys.storage }),
    client.invalidateQueries({ queryKey: platformKeys.audit }),
    invalidateTenantViews(client, tenantId),
  ])
}

export function useTenantProviders() {
  const { tenantId } = useTenant()
  return useQuery({ queryKey: storageConnectionKeys.tenantProviders(tenantId), queryFn: ({ signal }) => getTenantProviders(tenantId, signal) })
}

export function useTenantConnections() {
  const { tenantId } = useTenant()
  return useQuery({ queryKey: storageConnectionKeys.tenantList(tenantId), queryFn: ({ signal }) => getTenantConnections(tenantId, signal) })
}

export function usePlatformProviders() {
  return useQuery({ queryKey: storageConnectionKeys.platformProviders, queryFn: ({ signal }) => getPlatformProviders(signal) })
}

export function usePlatformConnections(filters: PlatformConnectionFilters) {
  return useQuery({ queryKey: storageConnectionKeys.platformList(filters), queryFn: ({ signal }) => getPlatformConnections(filters, signal), placeholderData: keepPreviousData })
}

export function usePlatformTenantConnections(tenantId: string) {
  return useQuery({ queryKey: storageConnectionKeys.platformTenantList(tenantId), queryFn: ({ signal }) => getPlatformTenantConnections(tenantId, signal), enabled: Boolean(tenantId) })
}

export interface UpdateVariables { connection: ConnectionView; input: PlatformUpdateConnectionInput }
export interface SecretVariables { connection: ConnectionView; values: SecretValues }
export interface EnabledVariables { connection: ConnectionView; enabled: boolean }

/**
 * What the row actions need, identical for the tenant and the platform route. Every mutation refreshes on success AND failure: a test
 * records its result either way and a stale permission set must not survive a 409.
 */
export interface ConnectionOps {
  scope: 'tenant' | 'platform'
  update: UseMutationResult<ConnectionView, unknown, UpdateVariables>
  setSecret: UseMutationResult<ConnectionView, unknown, SecretVariables>
  clearSecret: UseMutationResult<ConnectionView, unknown, ConnectionView>
  test: UseMutationResult<ConnectionTestResponse, unknown, ConnectionView>
  setEnabled: UseMutationResult<ConnectionView, unknown, EnabledVariables>
  remove: UseMutationResult<void, unknown, ConnectionView>
}

export function useTenantConnectionOps(): ConnectionOps {
  const { tenantId } = useTenant()
  const client = useQueryClient()
  const onSettled = () => invalidateTenantViews(client, tenantId)
  return {
    scope: 'tenant',
    update: useMutation({ mutationFn: ({ connection, input }: UpdateVariables) => updateTenantConnection(tenantId, connection.id, { name: input.name, configuration: input.configuration }), onSettled }),
    // gcTime 0: the mutation (and the secret values in its variables) leaves the cache as soon as the dialog unmounts.
    setSecret: useMutation({ mutationFn: ({ connection, values }: SecretVariables) => setTenantConnectionSecret(tenantId, connection.id, values), onSettled, gcTime: 0 }),
    clearSecret: useMutation({ mutationFn: (connection: ConnectionView) => clearTenantConnectionSecret(tenantId, connection.id), onSettled }),
    test: useMutation({ mutationFn: (connection: ConnectionView) => testTenantConnection(tenantId, connection.id), onSettled }),
    setEnabled: useMutation({ mutationFn: ({ connection, enabled }: EnabledVariables) => setTenantConnectionEnabled(tenantId, connection.id, enabled), onSettled }),
    remove: useMutation({ mutationFn: (connection: ConnectionView) => deleteTenantConnection(tenantId, connection.id), onSettled }),
  }
}

export function usePlatformConnectionOps(): ConnectionOps {
  const client = useQueryClient()
  const onSettled = (_data: unknown, _error: unknown, variables: ConnectionView | { connection: ConnectionView }) =>
    invalidatePlatformViews(client, 'connection' in variables ? variables.connection.tenantId : variables.tenantId)
  return {
    scope: 'platform',
    update: useMutation({ mutationFn: ({ connection, input }: UpdateVariables) => updatePlatformConnection(connection.id, input), onSettled }),
    setSecret: useMutation({ mutationFn: ({ connection, values }: SecretVariables) => setPlatformConnectionSecret(connection.id, values), onSettled, gcTime: 0 }),
    clearSecret: useMutation({ mutationFn: (connection: ConnectionView) => clearPlatformConnectionSecret(connection.id), onSettled }),
    test: useMutation({ mutationFn: (connection: ConnectionView) => testPlatformConnection(connection.id), onSettled }),
    setEnabled: useMutation({ mutationFn: ({ connection, enabled }: EnabledVariables) => setPlatformConnectionEnabled(connection.id, enabled), onSettled }),
    remove: useMutation({ mutationFn: (connection: ConnectionView) => deletePlatformConnection(connection.id), onSettled }),
  }
}

/** Creating can carry secret values: same gcTime 0 rule. */
export function useCreateTenantConnection() {
  const { tenantId } = useTenant()
  const client = useQueryClient()
  return useMutation({
    mutationFn: (input: TenantCreateConnectionInput) => createTenantConnection(tenantId, input),
    onSettled: () => invalidateTenantViews(client, tenantId),
    gcTime: 0,
  })
}

export function useCreatePlatformConnection() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: ({ tenantId, input }: { tenantId: string; input: PlatformCreateConnectionInput }) => createPlatformConnection(tenantId, input),
    onSettled: (_data, _error, variables) => invalidatePlatformViews(client, variables.tenantId),
    gcTime: 0,
  })
}
