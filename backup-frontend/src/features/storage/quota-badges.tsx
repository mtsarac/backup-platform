import { Badge } from '@/components/ui/badge'
import { isLegacyUnlimited, quotaStateMeta, subscriptionStatusMeta } from './labels'
import type { EntitlementStatus, PlanSummary, QuotaState } from './types'

export function QuotaStateBadge({ state }: { state: QuotaState | string }) {
  const meta = Object.hasOwn(quotaStateMeta, state) ? quotaStateMeta[state as QuotaState] : { label: state, tone: 'neutral' as const }
  return <Badge tone={meta.tone}>{meta.label}</Badge>
}

export function EntitlementStatusBadge({ status }: { status: EntitlementStatus | string }) {
  const meta = Object.hasOwn(subscriptionStatusMeta, status) ? subscriptionStatusMeta[status as EntitlementStatus] : { label: status, tone: 'neutral' as const }
  return <Badge tone={meta.tone}>{meta.label}</Badge>
}

/** Plan name, with the system plan every pre-quota tenant sits on called what it is. */
export function PlanName({ plan }: { plan: PlanSummary | null }) {
  if (!plan) return <span className="text-text-muted">Plan yok</span>
  return <span className="inline-flex flex-wrap items-center gap-1.5"><span className="font-medium">{plan.name}</span>{isLegacyUnlimited(plan) && <Badge tone="warning">Legacy Unlimited</Badge>}</span>
}
