import { tx } from '@/lib/i18n'
import { formatGb } from './storage'
import type { Subscription } from './types'

export function assignedByLabel(subscription: Subscription) {
  if (subscription.assignedByUserId === null) return tx('Sistem (geçiş)')
  return subscription.assignedByDisplayName ?? tx('Bilinmeyen kullanıcı')
}

export function overrideLabel(override: Subscription['storageLimitOverride']) {
  if (override.kind === 'limited') return override.bytes === null ? tx('Sınırlı') : formatGb(override.bytes)
  if (override.kind === 'unlimited') return tx('Limitsiz')
  return tx('Yok')
}

export function sourceLabel(source: Subscription['effectiveStorageLimit']['source']) {
  return source === 'override' ? tx('Override') : 'Plan'
}
