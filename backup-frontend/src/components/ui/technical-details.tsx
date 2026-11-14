import { useLayoutEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { Dialog } from '@base-ui/react/dialog'
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
  /** Richer technical content (tables, cards) shown below the items. */
  children?: ReactNode
  /** Inline accordion instead of the centered modal. Used automatically inside a dialog, so a modal never opens another modal. */
  accordion?: boolean
}

export function TechnicalDetails(props: TechnicalDetailsProps) {
  const anchor = useRef<HTMLSpanElement>(null)
  const [insideDialog, setInsideDialog] = useState(false)
  useLayoutEffect(() => { setInsideDialog(!!anchor.current?.closest('[role="dialog"]')) }, [])
  const inline = props.accordion || insideDialog
  return <>
    <span ref={anchor} hidden />
    {inline ? <TechnicalAccordion {...props} /> : <TechnicalModal {...props} />}
  </>
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

const triggerClass = 'w-fit cursor-pointer select-none rounded-sm py-1 text-sm font-medium text-text-muted underline-offset-4 hover:text-text hover:underline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent'

/** Centered dialog for identifiers, raw codes and system internals. Never opens by itself unless `defaultOpen` asks for it. */
function TechnicalModal({ items, defaultOpen = false, children }: TechnicalDetailsProps) {
  useLanguage()
  const [open, setOpen] = useState(defaultOpen)
  const [query, setQuery] = useState('')
  const jsonPayload = useMemo(() => JSON.stringify(Object.fromEntries(items.map(item => [item.label, item.value])), null, 2), [items])
  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase()
    return q ? items.filter(item => item.label.toLowerCase().includes(q) || item.value.toLowerCase().includes(q)) : items
  }, [items, query])
  if (items.length === 0 && !children) return null
  return <Dialog.Root open={open} onOpenChange={(next) => { setOpen(next); if (!next) setQuery('') }}>
    <Dialog.Trigger className={triggerClass}>{tx('Teknik ayrıntılar')}</Dialog.Trigger>
    <Dialog.Portal>
      <Dialog.Backdrop className="fixed inset-0 z-40 bg-black/50" />
      <Dialog.Popup className="fixed inset-0 z-50 flex flex-col overflow-hidden border border-border bg-surface text-left shadow-overlay sm:inset-auto sm:left-1/2 sm:top-1/2 sm:max-h-[85dvh] sm:w-[calc(100%-2rem)] sm:max-w-xl sm:-translate-x-1/2 sm:-translate-y-1/2 sm:rounded-xl">
        <div className="flex items-center justify-between gap-3 border-b border-border px-5 py-3.5">
          <div className="flex items-center gap-2.5">
            <span aria-hidden="true" className="flex size-7 items-center justify-center rounded-md border border-border bg-surface-2 text-text"><Terminal className="size-3.5" /></span>
            <Dialog.Title className="text-base font-semibold text-text-heading">{tx('Teknik ayrıntılar')}</Dialog.Title>
          </div>
          <div className="flex items-center gap-1">
            {items.length > 0 && <span className="flex items-center gap-1 text-xs text-text-muted">{tx('Tümünü kopyala')}<CopyButton value={jsonPayload} label={tx('Tümünü kopyala')} /></span>}
            <Dialog.Close aria-label={tx('Kapat')} className="rounded-lg p-1.5 text-text-muted transition-colors hover:bg-surface-2 hover:text-text"><X aria-hidden="true" className="size-4" /></Dialog.Close>
          </div>
        </div>
        <Dialog.Description className="sr-only">{tx('Hata kodları, kimlikler ve sistem ayrıntıları. Destek ekibine iletmek için kopyalayabilirsiniz.')}</Dialog.Description>
        {items.length > 4 && <div className="border-b border-border bg-surface-2/30 px-5 py-2">
          <div className="relative">
            <Search aria-hidden="true" className="absolute left-2.5 top-1/2 size-3.5 -translate-y-1/2 text-text-muted" />
            <input type="search" aria-label={tx('Ara')} value={query} onChange={(event) => setQuery(event.target.value)} placeholder={tx('Ara')}
              className="w-full rounded-md border border-border bg-surface py-1.5 pl-8 pr-3 text-sm text-text placeholder:text-text-muted focus:border-accent focus:outline-hidden" />
          </div>
        </div>}
        <div className="min-h-0 flex-1 space-y-4 overflow-y-auto p-5">
          {items.length > 0 && (filtered.length === 0
            ? <p className="py-6 text-center text-sm text-text-muted">{tx('Aramayla eşleşen kayıt yok.')}</p>
            : <dl className="space-y-2.5">{filtered.map(item => <div key={item.label} className={`${rowClass} rounded-lg border border-border/70 bg-surface-2/30 p-3 text-sm`}>
              <dt className="col-start-1 font-medium text-text-heading">{item.label}</dt>
              <dd className="col-start-1 mt-0.5 max-h-40 overflow-y-auto whitespace-pre-wrap break-all font-mono text-xs text-text-muted">{item.value}</dd>
              <dd className="col-start-2 row-span-2 row-start-1"><CopyButton value={item.value} label={tx('Kopyala: {0}', { '0': item.label })} /></dd>
            </div>)}</dl>)}
          {children && <div className="space-y-3 border-t border-border pt-3 first:border-t-0 first:pt-0">{children}</div>}
        </div>
      </Dialog.Popup>
    </Dialog.Portal>
  </Dialog.Root>
}
