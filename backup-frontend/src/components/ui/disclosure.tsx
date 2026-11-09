import type { ReactNode } from 'react'
import { cn } from '@/lib/utils'

/** A collapsed-by-default section; `open` starts it expanded (for example when a hidden field has an error). */
export function Disclosure({ title, open, className, children }: { title: ReactNode; open?: boolean; className?: string; children: ReactNode }) {
  return <details open={open || undefined} className={cn('group text-sm', className)}>
    <summary className="w-fit pointer-coarse:min-h-11 cursor-pointer select-none py-1 font-medium text-text-muted underline-offset-4 hover:text-text hover:underline group-open:text-text-heading">{title}</summary>
    <div className="mt-2 border-l-2 border-border pl-4">{children}</div>
  </details>
}
