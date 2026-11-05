import type { ReactNode } from 'react'
import type { LucideIcon } from 'lucide-react'
import { Card } from '@/components/ui/card'
import { EmptyState } from '@/components/ui/empty-state'

/** For a DataList placed inside a flush `Section` that already draws the surface: drops the table's own frame. */
export const bareListClass = 'rounded-none border-0 shadow-none'

/** An empty, unfiltered list: say what is missing and offer the way to add it. */
export function ListEmpty({ icon, message, action }: { icon?: LucideIcon; message: ReactNode; action?: ReactNode }) {
  return <Card padding="none"><EmptyState icon={icon} message={message} action={action} /></Card>
}
