import { useState, type ReactNode } from 'react'
import { ChevronRight } from 'lucide-react'
import { cn } from '@/lib/utils'

/** The one disclosure look, shared by `Disclosure` and `TechnicalDetails`: a chevron that turns, then the muted label. */
export const disclosureTriggerClass = 'flex w-fit cursor-pointer select-none items-center gap-1 rounded-sm py-1 font-medium text-text-muted underline-offset-4 hover:text-text hover:underline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent'
export function DisclosureChevron({ className }: { className?: string }) {
  return <ChevronRight aria-hidden="true" className={cn('size-3.5 shrink-0 transition-transform', className)} />
}

/**
 * A collapsed-by-default section. `open` opens it when it becomes true (a filter in it is set, a hidden field has an error), also after load or a link to it;
 * it never closes it again: only the reader does that.
 */
export function Disclosure({ title, open, className, children }: { title: ReactNode; open?: boolean; className?: string; children: ReactNode }) {
  const [expanded, setExpanded] = useState(Boolean(open))
  const [wanted, setWanted] = useState(Boolean(open))
  if (Boolean(open) !== wanted) { setWanted(Boolean(open)); if (open) setExpanded(true) }
  return <details open={expanded} onToggle={event => setExpanded(event.currentTarget.open)} className={cn('group text-sm', className)}>
    <summary className={cn(disclosureTriggerClass, 'list-none group-open:text-text-heading [&::-webkit-details-marker]:hidden')}>
      <DisclosureChevron className="group-open:rotate-90" />{title}
    </summary>
    <div className="mt-2 border-l-2 border-border pl-4">{children}</div>
  </details>
}
