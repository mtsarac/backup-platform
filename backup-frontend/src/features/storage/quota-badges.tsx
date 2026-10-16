import { tx, useLanguage } from '@/lib/i18n'
import { Badge } from '@/components/ui/badge'
import { isLegacyUnlimited, quotaStateMeta, subscriptionStatusMeta } from './labels'
import type { EntitlementStatus, PlanSummary, QuotaState } from './types'

export function QuotaStateBadge({ state }: { state: QuotaState | string }) {
  useLanguage()
  const meta = Object.hasOwn(quotaStateMeta, state) ? quotaStateMeta[state as QuotaState] : { label: state, tone: 'neutral' as const }
  return <Badge tone={meta.tone}>{tx(meta.label)}</Badge>
}

export function EntitlementStatusBadge({ status }: { status: EntitlementStatus | string }) {
  useLanguage()
  const meta = Object.hasOwn(subscriptionStatusMeta, status) ? subscriptionStatusMeta[status as EntitlementStatus] : { label: status, tone: 'neutral' as const }
  return <Badge tone={meta.tone}>{tx(meta.label)}</Badge>
}

/** Plan name, with the system plan every pre-quota tenant sits on called what it is. */
export function PlanName({ plan }: { plan: PlanSummary | null }) {
  useLanguage()
  if (!plan) return <span className="text-text-muted">{tx("Plan yok")}</span>
  return <span className="inline-flex flex-wrap items-center gap-1.5"><span className="font-medium">{plan.name}</span>{isLegacyUnlimited(plan) && <Badge tone="warning">{tx("Legacy Unlimited")}</Badge>}</span>
}
