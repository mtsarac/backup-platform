import { useEffect, useMemo, useState, type ReactNode } from 'react'
import { Search, Terminal, X } from 'lucide-react'
import { tx, useLanguage } from '@/lib/i18n'
import { CopyButton } from '@/components/ui/copy-button'

export interface TechnicalItem {
  label: string
  value: string
}

export interface TechnicalDetailsProps {
  items: readonly TechnicalItem[]
  defaultOpen?: boolean
  /** Richer technical content (tables, cards) shown in the drawer. */
  children?: ReactNode
}

/**
 * Slide-over inspector drawer for identifiers, raw codes, and system internals.
 * Minimal on-page footprint; rich searchable inspector when opened.
 */
export function TechnicalDetails({ items, defaultOpen = false, children }: TechnicalDetailsProps) {
  useLanguage()
  const [open, setOpen] = useState(defaultOpen)
  const [query, setQuery] = useState('')
  useEffect(() => {
    if (!open) return
    function handleKeyDown(e: KeyboardEvent) {
      if (e.key === 'Escape') setOpen(false)
    }
    window.addEventListener('keydown', handleKeyDown)
    return () => window.removeEventListener('keydown', handleKeyDown)
  }, [open])

  const jsonPayload = useMemo(() => {
    const record: Record<string, string> = {}
    for (const item of items) record[item.label] = item.value
    return JSON.stringify(record, null, 2)
  }, [items])

  const filteredItems = useMemo(() => {
    const q = query.trim().toLowerCase()
    if (!q) return items
    return items.filter(
      (item) => item.label.toLowerCase().includes(q) || item.value.toLowerCase().includes(q)
    )
  }, [items, query])

  if (items.length === 0 && !children) return null

  return (
    <details
      open={open || undefined}
      onToggle={(e) => {
        setOpen(e.currentTarget.open)
      }}
      className="group rounded-lg border border-border bg-surface-2 text-sm text-left"
    >
      <summary
        className="cursor-pointer select-none rounded-lg px-3 py-2 font-medium text-text-heading"
      >
        {tx('Teknik ayrıntılar')}
      </summary>

      {/* Backdrop (visible only when open) */}
      {open && (
        <div
          className="fixed inset-0 z-40 bg-black/40 backdrop-blur-xs transition-opacity animate-in fade-in"
          onClick={(e) => {
            e.preventDefault()
            setOpen(false)
          }}
          aria-hidden="true"
        />
      )}

      {/* Drawer Panel: hidden when closed, fixed slide-over when open */}
      <div
        className={
          open
            ? 'fixed inset-y-0 right-0 z-50 flex w-full max-w-md flex-col border-l border-border bg-surface shadow-2xl transition-transform animate-in slide-in-from-right duration-200'
            : 'hidden'
        }
      >
        {/* Drawer Header */}
        <div className="flex items-center justify-between border-b border-border px-5 py-3.5">
          <div className="flex items-center gap-2.5">
            <div className="flex size-7 items-center justify-center rounded-md bg-surface-2 border border-border text-text">
              <Terminal className="size-3.5" />
            </div>
            {open && (
              <h3 className="text-sm font-semibold text-text-heading">
                {tx('Ayrıntı')}
              </h3>
            )}
          </div>

          <div className="flex items-center gap-2">
            {open && items.length > 0 && (
              <CopyButton value={jsonPayload} label={tx('Kopyala: {0}', { '0': 'JSON' })} />
            )}
            {open && (
              <button
                type="button"
                onClick={() => setOpen(false)}
                className="rounded-lg p-1.5 text-text-muted hover:bg-surface-2 hover:text-text transition-colors"
                aria-label={tx('Kapat')}
              >
                <X className="size-4" />
              </button>
            )}
          </div>
        </div>

        {/* Live Search */}
        {open && items.length > 4 && (
          <div className="border-b border-border bg-surface-2/30 px-5 py-2">
            <div className="relative">
              <Search className="absolute left-2.5 top-1/2 -translate-y-1/2 size-3.5 text-text-muted" />
              <input
                type="search"
                value={query}
                onChange={(e) => setQuery(e.target.value)}
                placeholder={tx('Ara')}
                className="w-full rounded-md border border-border bg-surface py-1 pl-8 pr-7 text-xs text-text placeholder:text-text-muted focus:border-accent focus:outline-hidden"
              />
              {query && (
                <button
                  type="button"
                  onClick={() => setQuery('')}
                  className="absolute right-2 top-1/2 -translate-y-1/2 p-0.5 text-text-muted hover:text-text"
                >
                  <X className="size-3" />
                </button>
              )}
            </div>
          </div>
        )}

        {/* Items in Drawer */}
        <dl className="flex-1 overflow-y-auto p-5 space-y-2.5">
          {filteredItems.length === 0 ? (
            <p className="py-8 text-center text-xs text-text-muted">
              {tx('Aramayla eşleşen kayıt yok.')}
            </p>
          ) : (
            filteredItems.map((item) => (
              <div
                key={item.label}
                className="flex flex-wrap items-center justify-between gap-2 rounded-lg border border-border/70 bg-surface-2/30 p-2.5 text-xs transition-colors hover:border-border hover:bg-surface-2/60"
              >
                <div className="min-w-0 flex-1">
                  <dt className="font-medium text-text-heading">{item.label}</dt>
                  <dd className="font-mono text-[11px] text-text break-all select-all text-text-muted mt-0.5">
                    {item.value}
                  </dd>
                </div>
                <CopyButton value={item.value} label={tx('Kopyala: {0}', { '0': item.label })} />
              </div>
            ))
          )}

          {children && (
            <div className="mt-4 border-t border-border pt-3 space-y-3">
              {children}
            </div>
          )}
        </dl>
      </div>
    </details>
  )
}
