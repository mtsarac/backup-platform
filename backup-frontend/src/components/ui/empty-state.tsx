import type { ReactNode } from 'react'
import type { LucideIcon } from 'lucide-react'
import { tx, useLanguage } from '@/lib/i18n'
import { Button } from '@/components/ui/button'

/**
 * An empty list: one sentence about why, then the one primary action. Filters that hid everything replace the action with "Filtreleri temizle"
 * (clearing needs no permission); without permission the action is replaced by who to ask.
 */
export function EmptyState({ icon: Icon, message, action, canAct = true, onClearFilters }: { icon?: LucideIcon; message: ReactNode; action?: ReactNode; canAct?: boolean; onClearFilters?: () => void }) {
  useLanguage()
  return <div className="flex flex-col items-center gap-2 px-6 py-8 text-center">
    {Icon && <Icon aria-hidden="true" className="size-5 text-text-muted" />}
    <p className="max-w-md font-medium text-text-heading">{message}</p>
    {onClearFilters ? <Button type="button" variant="outline" onClick={onClearFilters}>{tx("Filtreleri temizle")}</Button> : canAct ? action : <p className="text-sm text-text-muted">{tx("Yöneticinize danışın")}</p>}
  </div>
}
