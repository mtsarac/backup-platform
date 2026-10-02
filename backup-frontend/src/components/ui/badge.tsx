import { cva, type VariantProps } from 'class-variance-authority'
import type { ReactNode } from 'react'
import { cn } from '@/lib/utils'
import type { AgentStatus, BackupStatus } from '@/lib/types'

const badgeVariants = cva(
  'inline-flex items-center gap-1.5 rounded-full px-2 py-0.5 text-xs font-medium',
  {
    variants: {
      tone: {
        neutral: 'bg-muted text-muted-foreground',
        accent: 'bg-accent text-accent-foreground',
        success: 'bg-success-subtle text-success',
        warning: 'bg-warning-subtle text-warning',
        error: 'bg-error-subtle text-error',
        info: 'bg-info-subtle text-info',
      },
    },
    defaultVariants: {
      tone: 'neutral',
    },
  }
)

type BadgeProps = {
  children: ReactNode
  className?: string
} & VariantProps<typeof badgeVariants>

export function Badge({ tone, className, children }: BadgeProps) {
  return (
    <span className={cn(badgeVariants({ tone }), className)}>{children}</span>
  )
}

type StatusTone = NonNullable<VariantProps<typeof badgeVariants>['tone']>

function statusMeta(status: AgentStatus | BackupStatus): {
  label: string
  tone: StatusTone
} {
  switch (status) {
    case 'online':
      return { label: 'Çevrimiçi', tone: 'success' }
    case 'offline':
      return { label: 'Çevrimdışı', tone: 'error' }
    case 'waiting':
      return { label: 'Bekliyor', tone: 'warning' }
    case 'queued':
      return { label: 'Sırada', tone: 'warning' }
    case 'running':
      return { label: 'Çalışıyor', tone: 'info' }
    case 'completed':
      return { label: 'Tamamlandı', tone: 'success' }
    case 'failed':
      return { label: 'Başarısız', tone: 'error' }
    case 'cancelled':
      return { label: 'İptal Edildi', tone: 'neutral' }
    default:
      return assertNever(status)
  }
}

function assertNever(value: never): never {
  throw new Error(`Unexpected status: ${String(value)}`)
}

interface StatusBadgeProps {
  status: AgentStatus | BackupStatus
  className?: string
}

export function StatusBadge({ status, className }: StatusBadgeProps) {
  const { label, tone } = statusMeta(status)
  return (
    <Badge tone={tone} className={className}>
      {label}
    </Badge>
  )
}
