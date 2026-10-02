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
  className?: string
}

export function KpiCard({ label, value, icon, tone, className }: KpiCardProps) {
  return (
    <div
      className={cn(
        'rounded-lg border border-border bg-surface p-4',
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
      <div className="mt-1 text-2xl font-semibold text-text-heading">
        {value}
      </div>
    </div>
  )
}
