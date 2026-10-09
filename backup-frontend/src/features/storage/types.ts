/** Wire types of the quota API. Every value is the backend's; the UI derives nothing authoritative from them (state, thresholds, remaining). */
export type QuotaState = 'Normal' | 'Warning' | 'Critical' | 'OverQuota' | 'Unlimited'
export type EntitlementStatus = 'Active' | 'Suspended' | 'Expired' | 'NoSubscription'
export type AccountingMode = 'LogicalArtifactBytes' | 'PhysicalReplicaBytes'
export type EnforcementMode = 'BlockNewBackups' | 'WarnOnly' | 'None'

export const quotaStates: readonly QuotaState[] = ['Normal', 'Warning', 'Critical', 'OverQuota', 'Unlimited']
export const entitlementStatuses: readonly EntitlementStatus[] = ['Active', 'Suspended', 'Expired', 'NoSubscription']
export const accountingModes: readonly AccountingMode[] = ['LogicalArtifactBytes', 'PhysicalReplicaBytes']
export const enforcementModes: readonly EnforcementMode[] = ['BlockNewBackups', 'WarnOnly', 'None']

/** The selector kinds the backend accepts: `quotaScope` matches a connection's scope label, `storageConnection` matches connection GUIDs. */
export type ScopeKind = 'all' | 'providerType' | 'ownershipType' | 'quotaScope' | 'storageConnection'
export interface ScopeSelector {
  kind: ScopeKind
  values?: string[]
}

export interface PlanSummary {
  name: string
  isSystem: boolean
  systemKey: string | null
  subscriptionEndsAt: string | null
}

export const LEGACY_UNLIMITED_KEY = 'legacy_unlimited'

interface QuotaCommon {
  /** null = no limit (unlimited plan/override, or no subscription). */
  limitBytes: number | null
  unlimited: boolean
  usedBytes: number
  reservedBytes: number
  remainingBytes: number | null
  state: QuotaState
  warningThreshold: number
  criticalThreshold: number
  accountingMode: AccountingMode
  enforcementMode: EnforcementMode
  subscriptionStatus: EntitlementStatus
  newBackupsAllowed: boolean
  /** `quota_exceeded` or `subscription_required` when new backups are refused; the two are different problems. */
  denialCode: string | null
  plan: PlanSummary | null
  includedStorageScopes: ScopeSelector[]
  calculatedAt: string
}

/** `GET /tenants/{id}/storage-quota`: the tenant's own standing. */
export type TenantQuota = QuotaCommon

/** `GET /platform/tenants/{id}/quota`. */
export interface PlatformTenantQuota extends QuotaCommon {
  tenantId: string
  policy: { policyId: string; key: string; version: number }
}

/** One row of `GET /platform/storage-usage`. Used/Reserved/state are null while the tenant has no stored projection. */
export interface StorageUsageRow {
  tenantId: string
  tenantName: string
  plan: PlanSummary | null
  subscriptionStatus: EntitlementStatus
  limitBytes: number | null
  unlimited: boolean
  usedBytes: number | null
  reservedBytes: number | null
  remainingBytes: number | null
  state: QuotaState | null
  warningThreshold: number
  criticalThreshold: number
  accountingMode: AccountingMode
  enforcementMode: EnforcementMode
  newBackupsAllowed: boolean
  denialCode: string | null
  policy: { policyId: string; key: string; version: number }
  /** false: nothing has been calculated for this tenant yet. */
  hasUsage: boolean
  /** false: the stored figures were computed under another definition of usage than the tenant's current policy. */
  usageCurrent: boolean
  calculatedAt: string | null
}

export interface StorageUsageFilters {
  search?: string
  quotaState?: QuotaState
  subscriptionStatus?: EntitlementStatus
  page?: number
  pageSize?: number
}

export interface QuotaPolicy {
  policyId: string
  key: string
  name: string
  version: number
  accountingMode: AccountingMode
  includedStorageScopes: ScopeSelector[]
  enforcementMode: EnforcementMode
  warningThresholdPercent: number
  criticalThresholdPercent: number
  enabled: boolean
  isDefault: boolean
  createdAt: string
  updatedAt: string
}

export interface QuotaPolicyInput {
  name: string
  accountingMode: AccountingMode
  includedStorageScopes: ScopeSelector[]
  enforcementMode: EnforcementMode
  warningThresholdPercent: number
  criticalThresholdPercent: number
  enabled: boolean
}

export interface CreateQuotaPolicyInput extends QuotaPolicyInput {
  key: string
}

export interface QuotaPolicyUpdateResult {
  policy: QuotaPolicy
  tenantsRecalculated: number
}

export interface UsageReconciliation {
  tenantId: string
  drifted: boolean
  previousUsedBytes: number | null
  previousReservedBytes: number | null
  previousPolicyVersion: number | null
  usedBytes: number
  reservedBytes: number
  policyId: string
  policyVersion: number
  reservationsReleased: number
}
