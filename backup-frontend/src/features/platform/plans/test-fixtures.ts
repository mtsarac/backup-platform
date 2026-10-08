import type { Plan, Subscription } from './types'

export function makePlan(overrides: Partial<Plan> = {}): Plan {
  return {
    planId: 'plan-5', name: '5 GB', enabled: true, storageLimitBytes: 5_000_000_000, quotaPolicyReference: null, policyConfigurationVersion: 1,
    isSystem: false, systemKey: null, tenantCount: 3, createdAt: '2026-10-01T10:00:00Z', updatedAt: '2026-10-05T10:00:00Z', ...overrides,
  }
}

export const legacyPlan = makePlan({ planId: 'plan-legacy', name: 'Legacy Unlimited', storageLimitBytes: null, isSystem: true, systemKey: 'legacy_unlimited', tenantCount: 7 })

export function makeSubscription(overrides: Partial<Subscription> = {}): Subscription {
  return {
    subscriptionId: 'sub-1', tenantId: 'tenant-a', planId: 'plan-5', planName: '5 GB', planStorageLimitBytes: 5_000_000_000, status: 'Active',
    startsAt: '2026-10-01T10:00:00Z', endsAt: null, storageLimitOverride: { kind: 'none', bytes: null },
    effectiveStorageLimit: { unlimited: false, bytes: 5_000_000_000, source: 'plan' }, assignedByUserId: 'admin-id', assignedByDisplayName: 'Platform Operatörü',
    createdAt: '2026-10-01T10:00:00Z', updatedAt: '2026-10-01T10:00:00Z', ...overrides,
  }
}
