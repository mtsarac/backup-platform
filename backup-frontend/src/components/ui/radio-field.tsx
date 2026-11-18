import type { InputHTMLAttributes, ReactNode } from 'react'
import { cn } from '@/lib/utils'

/** The one standalone radio row: a row with the label as the click area. */
export function RadioField({ label, className, ...input }: { label: ReactNode; className?: string } & Omit<InputHTMLAttributes<HTMLInputElement>, 'type' | 'className'>) {
  return <label className={cn('flex min-h-control items-center gap-3 text-body text-text', className)}>
    <input {...input} type="radio" className="size-5 shrink-0 accent-primary" />{label}
  </label>
}
