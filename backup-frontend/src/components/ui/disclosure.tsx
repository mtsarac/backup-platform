import { useState, type ReactNode } from 'react'
import { cn } from '@/lib/utils'

/**
 * A collapsed-by-default section. `open` opens it when it becomes true (a filter in it is set, a hidden field has an error), also after load or a link to it;
 * it never closes it again: only the reader does that.
 */
export function Disclosure({ title, open, className, children }: { title: ReactNode; open?: boolean; className?: string; children: ReactNode }) {
  const [expanded, setExpanded] = useState(Boolean(open))
  const [wanted, setWanted] = useState(Boolean(open))
  if (Boolean(open) !== wanted) { setWanted(Boolean(open)); if (open) setExpanded(true) }
  return <details open={expanded} onToggle={event => setExpanded(event.currentTarget.open)} className={cn('group text-sm', className)}>
    <summary className="w-fit cursor-pointer select-none py-1 font-medium text-text-muted underline-offset-4 hover:text-text hover:underline group-open:text-text-heading">{title}</summary>
    <div className="mt-2 border-l-2 border-border pl-4">{children}</div>
  </details>
}
