import type { ComponentPropsWithoutRef, ElementType } from 'react'
import { cn } from '@/lib/utils'

/** The one flat work surface: border and spacing carry hierarchy, never a shadow (see `--elevation-card`). */
export const cardClass = 'rounded-xl border bg-surface shadow-card'

const tones = { default: 'border-border', subtle: 'border-border bg-surface-2', danger: 'border-error/40' } as const
const paddings = { none: '', md: 'p-4', lg: 'p-6' } as const

type CardProps<T extends ElementType> = { as?: T; tone?: keyof typeof tones; padding?: keyof typeof paddings } & Omit<ComponentPropsWithoutRef<T>, 'as'>

/** A bordered surface. `as` picks the element (`section`, `article`, `li`, `form`); `padding="none"` is for tables and lists that bring their own rows. */
export function Card<T extends ElementType = 'div'>({ as, tone = 'default', padding = 'md', className, ...props }: CardProps<T>) {
  const Component: ElementType = as ?? 'div'
  return <Component className={cn(cardClass, tones[tone], paddings[padding], className)} {...props} />
}
