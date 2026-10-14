import type { PolicyInput } from './types'

export type ThresholdField = 'staleAfterHours' | 'agentOfflineAfterMinutes' | 'reminderIntervalHours' | 'maxNotificationsPerHour'

/** Inclusive ranges from the API contract. */
export const thresholdRanges: Record<ThresholdField, { min: number; max: number }> = {
  staleAfterHours: { min: 1, max: 720 },
  agentOfflineAfterMinutes: { min: 5, max: 1440 },
  reminderIntervalHours: { min: 0, max: 168 },
  maxNotificationsPerHour: { min: 1, max: 200 },
}

/** Thresholds are edited as text; the first invalid field is reported per field. */
export function validateThresholds(values: Record<ThresholdField, string>): Partial<Record<ThresholdField, string>> {
  const errors: Partial<Record<ThresholdField, string>> = {}
  for (const [field, { min, max }] of Object.entries(thresholdRanges) as [ThresholdField, { min: number; max: number }][]) {
    const text = values[field].trim()
    const number = Number(text)
    if (text === '' || !Number.isInteger(number) || number < min || number > max) errors[field] = `${min} ile ${max} arasında bir tam sayı girin.`
  }
  return errors
}

export function toThresholds(values: Record<ThresholdField, string>): Pick<PolicyInput, ThresholdField> {
  return {
    staleAfterHours: Number(values.staleAfterHours), agentOfflineAfterMinutes: Number(values.agentOfflineAfterMinutes),
    reminderIntervalHours: Number(values.reminderIntervalHours), maxNotificationsPerHour: Number(values.maxNotificationsPerHour),
  }
}
