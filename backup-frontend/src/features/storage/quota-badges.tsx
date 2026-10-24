import { tx, useLanguage } from '@/lib/i18n'
import { Badge } from '@/components/ui/badge'
import { isLegacyUnlimited } from './labels'
import type { PlanSummary } from './types'

/** Plan name, with the system plan every pre-quota tenant sits on called what it is. */
export function PlanName({ plan }: { plan: PlanSummary | null }) {
  useLanguage()
  if (!plan) return <span className="text-text-muted">{tx("Plan yok")}</span>
  return <span className="inline-flex flex-wrap items-center gap-1.5"><span className="font-medium">{plan.name}</span>{isLegacyUnlimited(plan) && <Badge tone="warning">{tx("Legacy Unlimited")}</Badge>}</span>
}
