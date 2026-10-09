import { Badge } from '@/components/ui/badge'
import type { SubscriptionStatus } from './types'

const statuses: Record<SubscriptionStatus, { label: string; tone: 'success' | 'warning' | 'error' | 'neutral' }> = {
  Active: { label: 'Etkin', tone: 'success' },
  Suspended: { label: 'Askıda', tone: 'warning' },
  Expired: { label: 'Süresi doldu', tone: 'error' },
  Superseded: { label: 'Değiştirildi', tone: 'neutral' },
}

export function SubscriptionStatusPill({ status }: { status: SubscriptionStatus }) {
  const meta = Object.hasOwn(statuses, status) ? statuses[status] : { label: status, tone: 'neutral' as const }
  return <Badge tone={meta.tone}>{meta.label}</Badge>
}
