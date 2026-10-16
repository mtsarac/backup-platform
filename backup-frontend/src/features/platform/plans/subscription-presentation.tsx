import { tx, useLanguage } from '@/lib/i18n'
import { Badge } from '@/components/ui/badge'
import type { SubscriptionStatus } from './types'

const statuses: Record<SubscriptionStatus, { label: string; tone: 'success' | 'warning' | 'error' | 'neutral' }> = {
  Active: { get label() { return tx("Etkin") }, tone: 'success' },
  Suspended: { get label() { return tx("Askıda") }, tone: 'warning' },
  Expired: { get label() { return tx("Süresi doldu") }, tone: 'error' },
  Superseded: { get label() { return tx("Değiştirildi") }, tone: 'neutral' },
}

export function SubscriptionStatusPill({ status }: { status: SubscriptionStatus }) {
  useLanguage()
  const meta = Object.hasOwn(statuses, status) ? statuses[status] : { label: status, tone: 'neutral' as const }
  return <Badge tone={meta.tone}>{tx(meta.label)}</Badge>
}
