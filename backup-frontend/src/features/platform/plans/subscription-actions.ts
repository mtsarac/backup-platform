import type { Subscription } from './types'

export type SubscriptionAction = 'assign' | 'change' | 'suspend' | 'resume' | 'override'

export interface SubscriptionActions {
  /** The one sensible action for the current state. */
  primary: SubscriptionAction
  /** Other valid actions, shown only on request. */
  others: SubscriptionAction[]
}

/** Only the verbs valid for the state (an expired or missing subscription can only be assigned a plan); the backend still decides (409). `null` = no subscription yet. */
export function subscriptionActions(subscription: Subscription | null): SubscriptionActions {
  switch (subscription?.status) {
    case 'Active': return { primary: 'change', others: ['suspend', 'override'] }
    case 'Suspended': return { primary: 'resume', others: ['change', 'override'] }
    default: return { primary: 'assign', others: [] }
  }
}
