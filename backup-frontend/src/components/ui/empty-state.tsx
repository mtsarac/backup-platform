import type { ReactNode } from 'react'
import type { LucideIcon } from 'lucide-react'

/** An empty list is an invitation: say what is missing, and offer the way to add it. */
export function EmptyState({ icon: Icon, title, children, action }: { icon?: LucideIcon; title: ReactNode; children?: ReactNode; action?: ReactNode }) {
  return <div className="flex flex-col items-center gap-3 px-6 py-12 text-center">
    {Icon && <span className="flex size-12 items-center justify-center rounded-full bg-surface-3 text-text-muted"><Icon aria-hidden="true" className="size-6" /></span>}
    <div className="max-w-md space-y-1"><p className="font-medium text-text-heading">{title}</p>{children && <p className="text-sm text-text-muted">{children}</p>}</div>
    {action}
  </div>
}
