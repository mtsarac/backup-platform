import type { ReactNode } from 'react'
import type { LucideIcon } from 'lucide-react'
import { tx, useLanguage } from '@/lib/i18n'

/** An empty list: one sentence about why, then the one primary action. Without permission the action is replaced by who to ask. */
export function EmptyState({ icon: Icon, message, action, canAct = true }: { icon?: LucideIcon; message: ReactNode; action?: ReactNode; canAct?: boolean }) {
  useLanguage()
  return <div className="flex flex-col items-center gap-3 px-6 py-12 text-center">
    {Icon && <span className="flex size-12 items-center justify-center rounded-full bg-surface-3 text-text-muted"><Icon aria-hidden="true" className="size-6" /></span>}
    <p className="max-w-md font-medium text-text-heading">{message}</p>
    {canAct ? action : <p className="text-sm text-text-muted">{tx("Yöneticinize danışın")}</p>}
  </div>
}
