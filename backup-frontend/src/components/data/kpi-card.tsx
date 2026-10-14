import type { ReactNode } from 'react'
import { cn } from '@/lib/utils'

type Tone = 'primary' | 'success' | 'warning' | 'error' | 'info'

const toneClasses: Record<Tone, string> = {
  primary: 'text-primary',
  success: 'text-success',
  warning: 'text-warning',
  error: 'text-error',
  info: 'text-info',
}

interface KpiCardProps {
  label: string
  value: ReactNode
  icon?: ReactNode
  tone?: Tone
  hint?: ReactNode
  className?: string
}

export function KpiCard({ label, value, icon, tone, hint, className }: KpiCardProps) {
  return (
    <div
      className={cn(
        'rounded-xl border border-border bg-surface p-4 shadow-card',
        className
      )}
    >
      <div className="flex items-center justify-between">
        <span className="text-sm text-text-muted">{label}</span>
        {icon && (
          <span className={cn('text-text-muted', tone && toneClasses[tone])}>
            {icon}
          </span>
        )}
      </div>
      <div className={cn('mt-1 text-3xl font-semibold tabular-nums', tone === 'error' || tone === 'warning' ? toneClasses[tone] : 'text-text-heading')}>
        {value}
      </div>
      {hint && <p className="mt-0.5 text-sm text-text-muted">{hint}</p>}
    </div>
  )
}
