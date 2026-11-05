import type { ComponentProps } from 'react'
import { controlClass } from '@/components/ui/form-styles'
import { cn } from '@/lib/utils'

/** A text control inside `Field`: spread the field's `control` props onto it. Full width, no top margin (the field spaces its label). */
export function TextInput({ className, type = 'text', ...props }: ComponentProps<'input'>) {
  return <input type={type} className={cn('w-full', controlClass, className)} {...props} />
}

/** A multi-line text control, same look as `TextInput`. */
export function TextArea({ className, ...props }: ComponentProps<'textarea'>) {
  return <textarea className={cn('w-full', controlClass, className)} {...props} />
}

/** A native select, same look as `TextInput`. */
export function Select({ className, ...props }: ComponentProps<'select'>) {
  return <select className={cn('w-full', controlClass, className)} {...props} />
}
