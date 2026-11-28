import { tx, useLanguage } from '@/lib/i18n'
import { useRef, type KeyboardEvent, type ReactNode } from 'react'
import { NavLink } from 'react-router-dom'
import { cn } from '@/lib/utils'
import { useScrollFade } from '@/hooks/use-scroll-fade'

const tabClass = (active: boolean) => cn('relative inline-flex min-h-control shrink-0 items-center gap-2 whitespace-nowrap border-b-2 px-1.5 sm:px-3 text-body font-medium transition-colors',
  active ? 'border-primary text-text-heading' : 'border-transparent text-text-muted hover:border-input hover:text-text')

/** Tabs that are real routes (NavLinks in a labelled nav, current tab gets aria-current): `label` names the nav; `items` are `{ to, label, end? }` — set `end` on an index tab. Render it above an `<Outlet />` in a layout route, as pages/storage/storage-layout.tsx does. */
export function RouteTabs({ label, items }: { label: string; items: readonly { to: string; label: ReactNode; end?: boolean }[] }) {
  useLanguage()
  const scroller = useScrollFade<HTMLUListElement>()
  return <nav aria-label={label} className="mb-4 border-b border-border">
    <ul ref={scroller.ref} style={scroller.style} className="flex gap-1 overflow-x-auto [scrollbar-width:none]">{items.map((item) => <li key={item.to} className="shrink-0">
      <NavLink to={item.to} end={item.end} className={({ isActive }) => tabClass(isActive)}>{tx(item.label)}</NavLink>
    </li>)}</ul>
  </nav>
}

/** In-page tabs with roving arrow-key focus. Panels are rendered by the caller (`aria-labelledby` = `${idPrefix}-tab-${value}`). */
export function Tabs<T extends string>({ label, value, onChange, items, idPrefix }: {
  label: string; value: T; onChange: (value: T) => void; items: readonly { value: T; label: ReactNode; disabled?: boolean }[]; idPrefix: string
}) {
  useLanguage()
  const scroller = useScrollFade<HTMLDivElement>()
  const refs = useRef(new Map<T, HTMLButtonElement>())
  function onKeyDown(event: KeyboardEvent) {
    const enabled = items.filter((item) => !item.disabled)
    if (enabled.length === 0) return
    const index = enabled.findIndex((item) => item.value === value)
    const step = event.key === 'ArrowRight' ? 1 : event.key === 'ArrowLeft' ? -1 : 0
    if (!step && event.key !== 'Home' && event.key !== 'End') return
    event.preventDefault()
    const next = enabled[event.key === 'Home' ? 0 : event.key === 'End' ? enabled.length - 1 : (index + step + enabled.length) % enabled.length]
    onChange(next.value)
    refs.current.get(next.value)?.focus()
  }
  return <div role="tablist" aria-label={label} onKeyDown={onKeyDown} ref={scroller.ref} style={scroller.style} className="mb-4 flex overflow-x-auto border-b border-border [scrollbar-width:none]">
    {items.map((item) => <button key={item.value} ref={(node) => { if (node) refs.current.set(item.value, node); else refs.current.delete(item.value) }} type="button" role="tab" id={`${idPrefix}-tab-${item.value}`}
      disabled={item.disabled} aria-selected={item.value === value} aria-controls={`${idPrefix}-panel-${item.value}`} tabIndex={item.value === value && !item.disabled ? 0 : -1}
      onClick={() => onChange(item.value)} className={cn(tabClass(item.value === value), 'disabled:cursor-not-allowed disabled:opacity-50')}>{tx(item.label)}</button>)}
  </div>
}
