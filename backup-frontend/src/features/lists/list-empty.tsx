import type { ReactNode } from 'react'
import type { LucideIcon } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { EmptyState } from '@/components/ui/empty-state'
import { tx, useLanguage } from '@/lib/i18n'

/** For a DataList placed inside a flush `Section` that already draws the surface: drops the table's own frame. */
export const bareListClass = 'rounded-none border-0'

/**
 * The empty list: nothing yet (`message`, with the way to add one as `action`), or, while `filtering`, nothing matches
 * (`filteredMessage`, default generic) with a clear button.
 */
export function ListEmpty({ icon, message, action, filtering, filteredMessage, onClear }: { icon?: LucideIcon; message: ReactNode; action?: ReactNode; filtering?: boolean; filteredMessage?: ReactNode; onClear?: () => void }) {
  useLanguage()
  return <Card padding="none">{filtering
    ? <EmptyState message={filteredMessage ?? tx('Aramayla eşleşen kayıt yok.')} action={onClear && <Button variant="outline" size="sm" onClick={onClear}>{tx('Filtreleri temizle')}</Button>} />
    : <EmptyState icon={icon} message={message} action={action} />}</Card>
}
