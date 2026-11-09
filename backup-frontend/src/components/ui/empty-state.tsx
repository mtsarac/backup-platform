import type { ReactNode } from 'react'
import type { LucideIcon } from 'lucide-react'
import { tx, useLanguage } from '@/lib/i18n'

/** An empty list: one sentence about why, then the one primary action. Without permission the action is replaced by who to ask. */
export function EmptyState({ icon: Icon, message, action, canAct = true }: { icon?: LucideIcon; message: ReactNode; action?: ReactNode; canAct?: boolean }) {
  useLanguage()
  return <div className="flex flex-col items-center gap-2 px-6 py-8 text-center">
    {Icon && <Icon aria-hidden="true" className="size-5 text-text-muted" />}
    <p className="max-w-md font-medium text-text-heading">{message}</p>
    {canAct ? action : <p className="text-sm text-text-muted">{tx("Yöneticinize danışın")}</p>}
  </div>
}
