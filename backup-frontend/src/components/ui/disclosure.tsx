import type { ReactNode } from 'react'
import { cn } from '@/lib/utils'

/** A collapsed-by-default section; `open` starts it expanded (for example when a hidden field has an error). */
export function Disclosure({ title, open, className, children }: { title: ReactNode; open?: boolean; className?: string; children: ReactNode }) {
  return <details open={open || undefined} className={cn('group rounded-lg border border-border bg-surface-2 text-sm', className)}>
    <summary className="min-h-11 cursor-pointer select-none rounded-lg px-3 py-2 font-medium text-text-heading">{title}</summary>
    <div className="border-t border-border p-4">{children}</div>
  </details>
}
