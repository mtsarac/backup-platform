import { describe, expect, it } from 'vitest'
import { makeSubscription } from './test-fixtures'
import { subscriptionActions } from './subscription-actions'

describe('subscription action matrix', () => {
  it('offers only assign without a subscription', () => {
    expect(subscriptionActions(null)).toEqual({ assign: 'assign', suspend: false, resume: false, override: false })
  })
  it('Active: change plan, suspend, override', () => {
    expect(subscriptionActions(makeSubscription({ status: 'Active' }))).toEqual({ assign: 'change', suspend: true, resume: false, override: true })
  })
  it('Suspended: change plan, resume, override', () => {
    expect(subscriptionActions(makeSubscription({ status: 'Suspended' }))).toEqual({ assign: 'change', suspend: false, resume: true, override: true })
  })
  it('Expired: assign only', () => {
    expect(subscriptionActions(makeSubscription({ status: 'Expired' }))).toEqual({ assign: 'assign', suspend: false, resume: false, override: false })
  })
})
