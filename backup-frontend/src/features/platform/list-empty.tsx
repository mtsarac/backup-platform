import type { ReactNode } from 'react'
import type { LucideIcon } from 'lucide-react'
import { EmptyState } from '@/components/ui/empty-state'

/** For a DataTable placed inside a flush `Section` that already draws the surface: drops the table's own frame. */
export const bareTableClass = 'rounded-none border-0 shadow-none'

/** An empty, unfiltered list: say what is missing and offer the way to add it. */
export function ListEmpty({ icon, title, children, action }: { icon?: LucideIcon; title: ReactNode; children?: ReactNode; action?: ReactNode }) {
  return <div className="rounded-xl border border-border bg-surface shadow-card"><EmptyState icon={icon} title={title} action={action}>{children}</EmptyState></div>
}
