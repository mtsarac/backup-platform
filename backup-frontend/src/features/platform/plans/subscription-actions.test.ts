import { describe, expect, it } from 'vitest'
import { makeSubscription } from './test-fixtures'
import { subscriptionActions } from './subscription-actions'

describe('subscription action matrix', () => {
  it('offers only assign without a subscription', () => {
    expect(subscriptionActions(null)).toEqual({ primary: 'assign', others: [] })
  })
  it('Active: change plan first, then suspend and override', () => {
    expect(subscriptionActions(makeSubscription({ status: 'Active' }))).toEqual({ primary: 'change', others: ['suspend', 'override'] })
  })
  it('Suspended: resume first, then change plan and override', () => {
    expect(subscriptionActions(makeSubscription({ status: 'Suspended' }))).toEqual({ primary: 'resume', others: ['change', 'override'] })
  })
  it('Expired: assign a new plan only', () => {
    expect(subscriptionActions(makeSubscription({ status: 'Expired' }))).toEqual({ primary: 'assign', others: [] })
  })
})
