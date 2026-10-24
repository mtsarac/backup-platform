import type { ReactNode } from 'react'
import { tx, useLanguage } from '@/lib/i18n'
import { CopyButton } from '@/components/ui/copy-button'

export interface TechnicalItem { label: string; value: string }

/** The one place for identifiers, raw codes and other internals: closed by default, every value copyable. */
export function TechnicalDetails({ items, defaultOpen = false, children }: { items: readonly TechnicalItem[]; defaultOpen?: boolean; /** Richer technical content (tables, cards) shown after the copyable values. */ children?: ReactNode }) {
  useLanguage()
  if (items.length === 0 && !children) return null
  return <details open={defaultOpen || undefined} className="group rounded-lg border border-border bg-surface-2 text-sm">
    <summary className="cursor-pointer select-none rounded-lg px-3 py-2 font-medium text-text-heading">{tx("Teknik ayrıntılar")}</summary>
    {items.length > 0 && <dl className="space-y-2 border-t border-border px-3 py-2">
      {items.map(item => <div key={item.label} className="flex flex-wrap items-center justify-between gap-2">
        <div className="min-w-0"><dt className="text-text-muted">{item.label}</dt><dd className="break-all font-mono text-text">{item.value}</dd></div>
        <CopyButton value={item.value} label={tx("Kopyala: {0}", { "0": item.label })} />
      </div>)}
    </dl>}
    {children && <div className="space-y-6 border-t border-border p-3">{children}</div>}
  </details>
}
