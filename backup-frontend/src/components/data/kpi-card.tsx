import { tx, useLanguage } from '@/lib/i18n'
import type { ReactNode } from 'react'
import { cn } from '@/lib/utils'

type Tone = 'primary' | 'success' | 'warning' | 'error' | 'info'

interface KpiCardProps {
  label: string
  value: ReactNode
  tone?: Tone
  hint?: ReactNode
  className?: string
}

/** A plain figure with its label. Colour only when the number is a problem (`error`, `warning`). */
export function KpiCard({ label, value, tone, hint, className }: KpiCardProps) {
  useLanguage()
  return (
    <div className={cn('border-l-2 border-border py-1 pl-3', tone === 'error' && 'border-error', tone === 'warning' && 'border-warning', className)}>
      <div className={cn('text-2xl font-semibold tabular-nums', tone === 'error' ? 'text-error' : tone === 'warning' ? 'text-warning' : 'text-text-heading')}>{value}</div>
      <div className="text-sm text-text-muted">{tx(label)}</div>
      {hint && <p className="mt-0.5 text-sm text-text-muted">{hint}</p>}
    </div>
  )
}
