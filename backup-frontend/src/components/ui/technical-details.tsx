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
  /** Inline accordion instead of the slide-over drawer; for places that are already an overlay, such as dialogs. */
  accordion?: boolean
}

export function TechnicalDetails(props: TechnicalDetailsProps) {
  return props.accordion ? <TechnicalAccordion {...props} /> : <TechnicalDrawer {...props} />
}

/** A term and its value on the left, its copy button on the right; the button sits in a second `dd` so every `dl` child stays a valid term/definition group. */
const rowClass = 'grid grid-cols-[minmax(0,1fr)_auto] items-center gap-x-2'

function TechnicalAccordion({ items, defaultOpen = false, children }: TechnicalDetailsProps) {
  useLanguage()
  if (items.length === 0 && !children) return null
  return <details open={defaultOpen || undefined} className="group text-sm">
    <summary className="w-fit cursor-pointer select-none py-1 font-medium text-text-muted underline-offset-4 hover:text-text hover:underline group-open:text-text-heading">{tx('Teknik ayrıntılar')}</summary>
    {items.length > 0 && <dl className="mt-2 space-y-2 border-l-2 border-border pl-4">
      {items.map(item => <div key={item.label} className={rowClass}>
        <dt className="col-start-1 text-text-muted">{item.label}</dt><dd className="col-start-1 break-all font-mono text-text">{item.value}</dd>
        <dd className="col-start-2 row-span-2 row-start-1"><CopyButton value={item.value} label={tx('Kopyala: {0}', { '0': item.label })} /></dd>
      </div>)}
    </dl>}
    {children && <div className="mt-2 space-y-6 border-l-2 border-border pl-4">{children}</div>}
  </details>
}

/** Slide-over inspector drawer for identifiers, raw codes, and system internals. */
function TechnicalDrawer({ items, defaultOpen = false, children }: TechnicalDetailsProps) {
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
      className="group text-left text-sm"
    >
      <summary
        className="w-fit cursor-pointer select-none py-1 font-medium text-text-muted underline-offset-4 hover:text-text hover:underline group-open:text-text-heading"
      >
        {tx('Teknik ayrıntılar')}
      </summary>

      {/* Backdrop (visible only when open) */}
      {open && (
        <div
          className="fixed inset-0 z-40 bg-black/40 transition-opacity animate-in fade-in"
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
            ? 'fixed inset-y-0 right-0 z-50 flex w-full max-w-md flex-col border-l border-border bg-surface shadow-overlay transition-transform animate-in slide-in-from-right duration-200'
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
        <div className="flex-1 overflow-y-auto p-5">
          {filteredItems.length === 0 ? (
            <p className="py-8 text-center text-xs text-text-muted">
              {tx('Aramayla eşleşen kayıt yok.')}
            </p>
          ) : (
            <dl className="space-y-2.5">
              {filteredItems.map((item) => (
                <div
                  key={item.label}
                  className={`${rowClass} rounded-lg border border-border/70 bg-surface-2/30 p-2.5 text-xs transition-colors hover:border-border hover:bg-surface-2/60`}
                >
                  <dt className="col-start-1 font-medium text-text-heading">{item.label}</dt>
                  <dd className="col-start-1 mt-0.5 break-all font-mono text-[11px] text-text-muted select-all">
                    {item.value}
                  </dd>
                  <dd className="col-start-2 row-span-2 row-start-1"><CopyButton value={item.value} label={tx('Kopyala: {0}', { '0': item.label })} /></dd>
                </div>
              ))}
            </dl>
          )}

          {children && (
            <div className="mt-4 border-t border-border pt-3 space-y-3">
              {children}
            </div>
          )}
        </div>
      </div>
    </details>
  )
}
