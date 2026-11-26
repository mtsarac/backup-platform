import { useEffect, useId, useRef, useState, type KeyboardEvent } from 'react'
import { X } from 'lucide-react'
import { foldSearch } from '@/features/lists/fold-search'
import { tx, useLanguage } from '@/lib/i18n'
import { filterControlClass } from '@/components/ui/filters'
import { cn } from '@/lib/utils'

export interface PickerOption {
  /** What `onChange` receives; never shown, so it may be an id. */
  value: string
  /** The name people search for and read. */
  label: string
  /** A second line that is searched too (a job name, a date). */
  description?: string
}

/**
 * Search the loaded names; `onSearchChange` can also load matching names from the server.
 * `selectedLabel` names a current `value` outside the loaded options.
 */
export function SearchablePicker({ label, options, value, onChange, selectedLabel, placeholder, disabled, onSearchChange }: {
  label: string; options: readonly PickerOption[]; value: string; onChange: (value: string) => void
  selectedLabel?: string; placeholder?: string; disabled?: boolean; onSearchChange?: (query: string) => void
}) {
  useLanguage()
  const id = useId()
  const input = useRef<HTMLInputElement>(null)
  const [query, setQuery] = useState<string | null>(null)
  const [open, setOpen] = useState(false)
  const [active, setActive] = useState(0)
  const chosen = options.find(option => option.value === value)?.label ?? selectedLabel ?? ''
  const needle = foldSearch(query ?? '')
  const matches = options.filter(option => foldSearch(`${option.label} ${option.description ?? ''}`).includes(needle))
  const current = Math.min(active, matches.length - 1)
  const expanded = open && !disabled
  const listed = expanded && matches.length > 0
  const activeId = listed ? `${id}-${current}` : undefined
  // The list scrolls, so the option the arrow keys moved to must be brought into view.
  useEffect(() => { if (activeId) document.getElementById(activeId)?.scrollIntoView?.({ block: 'nearest' }) }, [activeId])

  function close() { setOpen(false); setQuery(null); onSearchChange?.('') }
  function pick(option: PickerOption) { onChange(option.value); close() }
  function onKeyDown(event: KeyboardEvent<HTMLInputElement>) {
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault()
      if (!open) { setOpen(true); setActive(0); return }
      setActive(Math.max(0, Math.min(matches.length - 1, current + (event.key === 'ArrowDown' ? 1 : -1))))
    } else if (event.key === 'Enter' && open) {
      event.preventDefault()
      if (matches[current]) pick(matches[current])
    } else if (event.key === 'Escape' && open) {
      event.preventDefault()
      close()
    }
  }

  return <div className="relative block min-w-0 space-y-1 text-sm font-medium text-text-heading">
    <label id={`${id}-label`} htmlFor={id}>{tx(label)}</label>
    <div className="relative">
      <input ref={input} id={id} role="combobox" aria-expanded={listed} aria-controls={listed ? `${id}-list` : undefined} aria-autocomplete="list" autoComplete="off"
        aria-activedescendant={activeId}
        className={cn(filterControlClass, value && 'pr-11')} disabled={disabled} placeholder={placeholder && tx(placeholder)}
        value={query ?? chosen}
        onFocus={event => event.currentTarget.select()}
        onClick={() => setOpen(true)}
        onChange={event => { setQuery(event.target.value); onSearchChange?.(event.target.value); setOpen(true); setActive(0) }}
        onBlur={close} onKeyDown={onKeyDown} />
      {value && !disabled && <button type="button" aria-label={tx("Seçimi temizle")} className="absolute inset-y-0 right-0 flex min-w-control items-center justify-center rounded-r-lg text-text-muted hover:text-text"
        onClick={() => { onChange(''); close(); input.current?.focus() }}><X aria-hidden="true" className="size-4" /></button>}
    </div>
    {expanded && matches.length === 0 && <div role="status" className="absolute z-20 w-full rounded-lg border border-border bg-surface-raised px-3 py-2 font-normal text-text-muted shadow-overlay">{tx("Eşleşen sonuç yok")}</div>}
    {listed && <ul id={`${id}-list`} role="listbox" aria-labelledby={`${id}-label`} onMouseDown={event => event.preventDefault()}
      className="absolute z-20 max-h-64 w-full overflow-y-auto rounded-lg border border-border bg-surface-raised py-1 font-normal text-text shadow-overlay">
      {matches.map((option, index) => <li key={option.value} id={`${id}-${index}`} role="option" aria-selected={option.value === value} onClick={() => pick(option)}
        className={cn('min-h-control cursor-pointer px-3 py-2', index === current && 'bg-surface-2', option.value === value && 'font-medium')}>
        <span className="block break-words">{option.label}</span>
        {option.description && <span className="block break-words text-xs text-text-muted">{option.description}</span>}
      </li>)}
    </ul>}
  </div>
}
