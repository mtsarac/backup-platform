import { tx, useLanguage } from '@/lib/i18n'
import type { ReactNode } from 'react'
import { Button } from '@/components/ui/button'
import { controlClass } from '@/components/ui/form-styles'

/** Shared look for filter controls; pair with `FilterField`. */
export const filterControlClass = `${controlClass} w-full text-sm`

/** One row of filters above a list. `onClear` adds the standard "Filtreleri temizle" button. */
export function Filters({ label, children, onClear, footer }: { label: string; children: ReactNode; onClear?: () => void; footer?: ReactNode }) {
  useLanguage()
  return <section aria-label={label} className="space-y-3">
    <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">{children}</div>
    {(onClear || footer) && <div className="flex flex-wrap items-center gap-3">{onClear && <Button variant="outline" size="sm" onClick={onClear}>{tx("Filtreleri temizle")}</Button>}{footer}</div>}
  </section>
}

/** A visible label above a filter control. Give the control the same `aria-label` when tests or assistive tech address it by name. */
export function FilterField({ label, children, className }: { label: string; children: ReactNode; className?: string }) {
  useLanguage()
  return <label className={`block min-w-0 space-y-1 text-sm font-medium text-text-heading ${className ?? ''}`}>{tx(label)}{children}</label>
}
