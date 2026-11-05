import { tx, useLanguage } from '@/lib/i18n'
import type { PlanSummary } from './types'

export function PlanName({ plan }: { plan: PlanSummary | null }) {
  useLanguage()
  if (!plan) return <span className="text-text-muted">{tx("Plan yok")}</span>
  return <span className="font-medium">{plan.name}</span>
}
