import type { InputHTMLAttributes, ReactNode } from 'react'
import { cn } from '@/lib/utils'

/** The one standalone checkbox row: a 44px touch target with the label as the click area. */
export function CheckField({ label, className, ...input }: { label: ReactNode; className?: string } & Omit<InputHTMLAttributes<HTMLInputElement>, 'type' | 'className'>) {
  return <label className={cn('flex min-h-9 pointer-coarse:min-h-11 items-center gap-3 text-body text-text', className)}>
    <input {...input} type="checkbox" className="size-5 shrink-0 accent-primary" />{label}
  </label>
}
