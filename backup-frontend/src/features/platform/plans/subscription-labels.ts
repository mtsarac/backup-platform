import { formatGb } from './storage'
import type { Subscription } from './types'

export function assignedByLabel(subscription: Subscription) {
  if (subscription.assignedByUserId === null) return 'Sistem (geçiş)'
  return subscription.assignedByDisplayName ?? 'Bilinmeyen kullanıcı'
}

export function overrideLabel(override: Subscription['storageLimitOverride']) {
  if (override.kind === 'limited') return override.bytes === null ? 'Sınırlı' : formatGb(override.bytes)
  if (override.kind === 'unlimited') return 'Limitsiz'
  return 'Yok'
}

export function sourceLabel(source: Subscription['effectiveStorageLimit']['source']) {
  return source === 'override' ? 'Override' : 'Plan'
}
