import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import {
  assignSubscriptionPlan, createPlan, getPlans, getSubscriptionHistory, getTenantSubscription, resumeSubscription,
  setPlanEnabled, setStorageLimitOverride, suspendSubscription, updatePlan,
} from '../api'
import { platformKeys } from '../hooks'
import type { PageParams } from '../types'
import type { AssignSubscriptionInput, CreatePlanInput, PlanFilters, PlanInput, StorageLimitOverride } from './types'

/** Plan list for the Plans page and (with `enabled: 'true'`) the plan picker. The list always comes from the backend. */
export function usePlans(filters: PlanFilters, enabled = true) {
  return useQuery({ queryKey: platformKeys.planList(filters), queryFn: ({ signal }) => getPlans(filters, signal), enabled, placeholderData: keepPreviousData })
}

export function useTenantSubscription(tenantId: string) {
  return useQuery({ queryKey: platformKeys.subscription(tenantId), queryFn: ({ signal }) => getTenantSubscription(tenantId, signal), enabled: Boolean(tenantId) })
}

export function useSubscriptionHistory(tenantId: string, params: PageParams, enabled: boolean) {
  return useQuery({
    queryKey: platformKeys.subscriptionHistoryPage(tenantId, params),
    queryFn: ({ signal }) => getSubscriptionHistory(tenantId, params, signal),
    enabled: Boolean(tenantId) && enabled,
    placeholderData: keepPreviousData,
  })
}

type Client = ReturnType<typeof useQueryClient>

/** Plan edits change tenant counts and the effective limit of subscribers, so subscription views refresh too. */
function invalidatePlanViews(client: Client) {
  return Promise.all([
    client.invalidateQueries({ queryKey: platformKeys.plans }),
    client.invalidateQueries({ queryKey: platformKeys.tenants }),
    client.invalidateQueries({ queryKey: platformKeys.audit }),
  ])
}

export function useCreatePlan() {
  const client = useQueryClient()
  return useMutation({ mutationFn: (input: CreatePlanInput) => createPlan(input), onSuccess: () => invalidatePlanViews(client) })
}

export function useUpdatePlan(planId: string) {
  const client = useQueryClient()
  return useMutation({ mutationFn: (input: PlanInput) => updatePlan(planId, input), onSuccess: () => invalidatePlanViews(client) })
}

export function useSetPlanEnabled() {
  const client = useQueryClient()
  return useMutation({ mutationFn: (input: { planId: string; enabled: boolean }) => setPlanEnabled(input.planId, input.enabled), onSuccess: () => invalidatePlanViews(client) })
}

/** Refresh after success and after failure: a 409 means the view was stale (state changed elsewhere). */
function refreshSubscriptionViews(client: Client, tenantId: string) {
  return Promise.all([
    client.invalidateQueries({ queryKey: platformKeys.subscription(tenantId) }),
    client.invalidateQueries({ queryKey: platformKeys.subscriptionHistory(tenantId) }),
    client.invalidateQueries({ queryKey: platformKeys.plans }),
    client.invalidateQueries({ queryKey: platformKeys.audit }),
  ])
}

export function useAssignSubscriptionPlan(tenantId: string) {
  const client = useQueryClient()
  return useMutation({ mutationFn: (input: AssignSubscriptionInput) => assignSubscriptionPlan(tenantId, input), onSettled: () => refreshSubscriptionViews(client, tenantId) })
}

export function useSuspendSubscription(tenantId: string) {
  const client = useQueryClient()
  return useMutation({ mutationFn: () => suspendSubscription(tenantId), onSettled: () => refreshSubscriptionViews(client, tenantId) })
}

export function useResumeSubscription(tenantId: string) {
  const client = useQueryClient()
  return useMutation({ mutationFn: () => resumeSubscription(tenantId), onSettled: () => refreshSubscriptionViews(client, tenantId) })
}

export function useSetStorageLimitOverride(tenantId: string) {
  const client = useQueryClient()
  return useMutation({ mutationFn: (override: StorageLimitOverride) => setStorageLimitOverride(tenantId, override), onSettled: () => refreshSubscriptionViews(client, tenantId) })
}
