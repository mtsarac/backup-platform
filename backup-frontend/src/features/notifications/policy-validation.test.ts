import { describe, expect, it } from 'vitest'
import { toThresholds, validateThresholds, type ThresholdField } from './policy-validation'

const valid: Record<ThresholdField, string> = { staleAfterHours: '48', agentOfflineAfterMinutes: '15', reminderIntervalHours: '24', maxNotificationsPerHour: '30' }

describe('threshold validation', () => {
  it('accepts the defaults and both inclusive bounds', () => {
    expect(validateThresholds(valid)).toEqual({})
    expect(validateThresholds({ staleAfterHours: '1', agentOfflineAfterMinutes: '5', reminderIntervalHours: '0', maxNotificationsPerHour: '1' })).toEqual({})
    expect(validateThresholds({ staleAfterHours: '720', agentOfflineAfterMinutes: '1440', reminderIntervalHours: '168', maxNotificationsPerHour: '200' })).toEqual({})
  })

  it.each([
    ['staleAfterHours', '0'], ['staleAfterHours', '721'],
    ['agentOfflineAfterMinutes', '4'], ['agentOfflineAfterMinutes', '1441'],
    ['reminderIntervalHours', '-1'], ['reminderIntervalHours', '169'],
    ['maxNotificationsPerHour', '0'], ['maxNotificationsPerHour', '201'],
    ['staleAfterHours', ''], ['staleAfterHours', '1.5'], ['staleAfterHours', 'abc'],
  ] as const)('rejects %s = %s', (field, value) => {
    expect(Object.keys(validateThresholds({ ...valid, [field]: value }))).toEqual([field])
  })

  it('converts to numbers', () => {
    expect(toThresholds(valid)).toEqual({ staleAfterHours: 48, agentOfflineAfterMinutes: 15, reminderIntervalHours: 24, maxNotificationsPerHour: 30 })
  })
})
