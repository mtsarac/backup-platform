import { cva, type VariantProps } from 'class-variance-authority'
import type { ReactNode } from 'react'
import { cn } from '@/lib/utils'

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

