import { cva, type VariantProps } from 'class-variance-authority'
import type { ReactNode } from 'react'
import { cn } from '@/lib/utils'

const badgeVariants = cva(
  'inline-flex max-w-full items-center gap-1.5 rounded-full px-2 py-0.5 text-xs font-medium leading-5',
  {
    variants: {
      tone: {
        neutral: 'bg-neutral-subtle text-neutral',
        accent: 'bg-accent text-primary',
        success: 'bg-success-subtle text-success',
        warning: 'bg-warning-subtle text-warning',
        error: 'bg-error-subtle text-error',
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
  /** A leading status dot: a second cue next to the label, so state never relies on color alone. */
  dot?: boolean
} & VariantProps<typeof badgeVariants>

export function Badge({ tone, className, children, dot }: BadgeProps) {
  return (
    <span className={cn(badgeVariants({ tone }), className)}>
      {dot && <span aria-hidden="true" className="size-1.5 shrink-0 rounded-full bg-current" />}
      {children}
    </span>
  )
}
