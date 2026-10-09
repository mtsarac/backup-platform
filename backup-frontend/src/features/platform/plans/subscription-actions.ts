import type { Subscription, SubscriptionStatus } from './types'

export interface SubscriptionActions {
  /** Assign (no open subscription) or change plan (open subscription). */
  assign: 'assign' | 'change' | null
  suspend: boolean
  resume: boolean
  override: boolean
}

/** Only the verbs the API exposes; validity is still decided by the backend (409). `null` = no subscription yet. */
export function subscriptionActions(subscription: Subscription | null): SubscriptionActions {
  const status: SubscriptionStatus | null = subscription?.status ?? null
  switch (status) {
    case 'Active': return { assign: 'change', suspend: true, resume: false, override: true }
    case 'Suspended': return { assign: 'change', suspend: false, resume: true, override: true }
    default: return { assign: 'assign', suspend: false, resume: false, override: false }
  }
}
