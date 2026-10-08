import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import {
  addMembership, changeMembershipRole, createTenant, createUser, getAudit, getMemberships, getPlatformAgent,
  getPlatformAgents, getSummary, getTenant, getTenants, getUsers, removeMembership, renameTenant, setTenantEnrollment,
} from './api'
import type { PlanFilters } from './plans/types'
import type { AuditFilters, CreateUserInput, MembershipRole, PageParams, PlatformAgentFilters, SearchParams } from './types'

export const platformKeys = {
  all: ['platform'] as const,
  summary: ['platform', 'summary'] as const,
  tenants: ['platform', 'tenants'] as const,
  tenantList: (params: SearchParams) => ['platform', 'tenants', 'list', params] as const,
  tenant: (tenantId: string) => ['platform', 'tenants', 'detail', tenantId] as const,
  membershipPage: (tenantId: string, params: PageParams) => ['platform', 'tenants', 'memberships', tenantId, params] as const,
  users: ['platform', 'users'] as const,
  userList: (params: SearchParams) => ['platform', 'users', 'list', params] as const,
  agents: ['platform', 'agents'] as const,
  agentList: (filters: PlatformAgentFilters) => ['platform', 'agents', 'list', filters] as const,
  agent: (tenantId: string, agentId: string) => ['platform', 'agents', 'detail', tenantId, agentId] as const,
  plans: ['platform', 'plans'] as const,
  planList: (filters: PlanFilters) => ['platform', 'plans', 'list', filters] as const,
  subscription: (tenantId: string) => ['platform', 'tenants', 'subscription', tenantId] as const,
  subscriptionHistory: (tenantId: string) => ['platform', 'tenants', 'subscriptions', tenantId] as const,
  subscriptionHistoryPage: (tenantId: string, params: PageParams) => ['platform', 'tenants', 'subscriptions', tenantId, params] as const,
  audit: ['platform', 'audit'] as const,
  auditList: (filters: AuditFilters) => ['platform', 'audit', 'list', filters] as const,
}

export function usePlatformSummary() {
  return useQuery({ queryKey: platformKeys.summary, queryFn: ({ signal }) => getSummary(signal) })
}

export function usePlatformTenants(params: SearchParams) {
  return useQuery({ queryKey: platformKeys.tenantList(params), queryFn: ({ signal }) => getTenants(params, signal), placeholderData: keepPreviousData })
}

export function usePlatformTenant(tenantId: string) {
  return useQuery({ queryKey: platformKeys.tenant(tenantId), queryFn: ({ signal }) => getTenant(tenantId, signal), enabled: Boolean(tenantId) })
}

export function usePlatformMemberships(tenantId: string, params: PageParams) {
  return useQuery({ queryKey: platformKeys.membershipPage(tenantId, params), queryFn: ({ signal }) => getMemberships(tenantId, params, signal), enabled: Boolean(tenantId), placeholderData: keepPreviousData })
}

export function usePlatformUsers(params: SearchParams, enabled = true) {
  return useQuery({ queryKey: platformKeys.userList(params), queryFn: ({ signal }) => getUsers(params, signal), enabled, placeholderData: keepPreviousData })
}

export function usePlatformAgents(filters: PlatformAgentFilters) {
  return useQuery({ queryKey: platformKeys.agentList(filters), queryFn: ({ signal }) => getPlatformAgents(filters, signal), placeholderData: keepPreviousData })
}

export function usePlatformAgent(tenantId: string, agentId: string) {
  return useQuery({ queryKey: platformKeys.agent(tenantId, agentId), queryFn: ({ signal }) => getPlatformAgent(tenantId, agentId, signal), enabled: Boolean(tenantId && agentId) })
}

export function usePlatformAudit(filters: AuditFilters) {
  return useQuery({ queryKey: platformKeys.auditList(filters), queryFn: ({ signal }) => getAudit(filters, signal), placeholderData: keepPreviousData })
}

export function useCreateTenant() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: (name: string) => createTenant(name),
    onSuccess: () => Promise.all([
      client.invalidateQueries({ queryKey: platformKeys.tenants }),
      client.invalidateQueries({ queryKey: platformKeys.summary }),
      client.invalidateQueries({ queryKey: platformKeys.audit }),
    ]),
  })
}

export function useRenameTenant(tenantId: string) {
  const client = useQueryClient()
  return useMutation({
    mutationFn: (name: string) => renameTenant(tenantId, name),
    onSuccess: () => Promise.all([
      client.invalidateQueries({ queryKey: platformKeys.tenants }),
      client.invalidateQueries({ queryKey: platformKeys.agents }),
      client.invalidateQueries({ queryKey: platformKeys.audit }),
    ]),
  })
}

export function useSetTenantEnrollment(tenantId: string) {
  const client = useQueryClient()
  return useMutation({
    mutationFn: (enabled: boolean) => setTenantEnrollment(tenantId, enabled),
    onSuccess: () => Promise.all([
      client.invalidateQueries({ queryKey: platformKeys.tenants }),
      client.invalidateQueries({ queryKey: platformKeys.audit }),
    ]),
  })
}

/** The `tenants` prefix covers the membership pages and tenant detail; users carry membership counts. */
function invalidateMembershipViews(client: ReturnType<typeof useQueryClient>) {
  return Promise.all([
    client.invalidateQueries({ queryKey: platformKeys.tenants }),
    client.invalidateQueries({ queryKey: platformKeys.users }),
    client.invalidateQueries({ queryKey: platformKeys.audit }),
  ])
}

export function useAddMembership(tenantId: string) {
  const client = useQueryClient()
  return useMutation({
    mutationFn: (input: { userId: string; role: MembershipRole }) => addMembership(tenantId, input.userId, input.role),
    onSuccess: () => invalidateMembershipViews(client),
  })
}

export function useChangeMembershipRole(tenantId: string) {
  const client = useQueryClient()
  return useMutation({
    mutationFn: (input: { userId: string; role: MembershipRole }) => changeMembershipRole(tenantId, input.userId, input.role),
    onSuccess: () => invalidateMembershipViews(client),
  })
}

export function useRemoveMembership(tenantId: string) {
  const client = useQueryClient()
  return useMutation({
    mutationFn: (userId: string) => removeMembership(tenantId, userId),
    onSuccess: () => invalidateMembershipViews(client),
  })
}

export function useCreateUser() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: (input: CreateUserInput) => createUser(input),
    onSuccess: () => Promise.all([
      client.invalidateQueries({ queryKey: platformKeys.users }),
      client.invalidateQueries({ queryKey: platformKeys.summary }),
      client.invalidateQueries({ queryKey: platformKeys.audit }),
    ]),
  })
}
