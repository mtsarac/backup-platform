import type { PageParams } from '../types'

export interface Plan {
  planId: string
  name: string
  enabled: boolean
  /** null = unlimited. 1 GB = 1,000,000,000 bytes. */
  storageLimitBytes: number | null
  quotaPolicyReference: string | null
  policyConfigurationVersion: number
  isSystem: boolean
  systemKey: string | null
  tenantCount: number
  createdAt: string
  updatedAt: string
}

export type EnabledFilter = 'true' | 'false'

export interface PlanFilters extends PageParams {
  enabled?: EnabledFilter
  search?: string
}

/** Exactly one of `storageLimitBytes` / `unlimited` is sent. */
export interface PlanInput {
  name: string
  storageLimitBytes?: number
  unlimited?: boolean
  quotaPolicyReference?: string
}

export interface CreatePlanInput extends PlanInput {
  enabled: boolean
}

export type SubscriptionStatus = 'Active' | 'Suspended' | 'Expired' | 'Superseded'

export type OverrideKind = 'none' | 'limited' | 'unlimited'
export const overrideKinds: readonly OverrideKind[] = ['none', 'limited', 'unlimited']

export interface StorageLimitOverride {
  kind: OverrideKind
  bytes?: number | null
}

export interface EffectiveStorageLimit {
  unlimited: boolean
  bytes: number | null
  source: 'plan' | 'override'
}

export interface Subscription {
  subscriptionId: string
  tenantId: string
  planId: string
  planName: string
  planStorageLimitBytes: number | null
  status: SubscriptionStatus
  startsAt: string
  endsAt: string | null
  storageLimitOverride: { kind: OverrideKind; bytes: number | null }
  effectiveStorageLimit: EffectiveStorageLimit
  assignedByUserId: string | null
  assignedByDisplayName: string | null
  createdAt: string
  updatedAt: string
}

export interface TenantSubscription {
  tenantId: string
  subscription: Subscription | null
}

export interface AssignSubscriptionInput {
  planId: string
  storageLimitOverride?: StorageLimitOverride
  endsAt?: string
}

export interface AssignSubscriptionResult {
  outcome: 'assigned' | 'changed'
  subscription: Subscription
  previousSubscriptionId: string | null
}
