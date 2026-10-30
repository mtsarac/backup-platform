import type { ComponentPropsWithoutRef } from 'react'
import { controlClass } from '@/components/ui/form-styles'
import { cn } from '@/lib/utils'

/** A text control inside `Field`: spread the field's `control` props onto it. Full width, no top margin (the field spaces its label). */
export function TextInput({ className, type = 'text', ...props }: ComponentPropsWithoutRef<'input'>) {
  return <input type={type} className={cn('w-full', controlClass, className)} {...props} />
}
