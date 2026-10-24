import { useId, useRef, useState, type KeyboardEvent } from 'react'
import { X } from 'lucide-react'
import { currentLocale, tx, useLanguage } from '@/lib/i18n'
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

const normalize = (text: string) => text.toLocaleLowerCase(currentLocale())

/**
 * Pick one thing by typing part of its name instead of pasting an id. `options` is whatever the caller has loaded; the search is
 * client-side, so it only finds those. `selectedLabel` names a current `value` that is not among them.
 */
export function SearchablePicker({ label, options, value, onChange, selectedLabel, placeholder, disabled }: {
  label: string; options: readonly PickerOption[]; value: string; onChange: (value: string) => void
  selectedLabel?: string; placeholder?: string; disabled?: boolean
}) {
  useLanguage()
  const id = useId()
  const input = useRef<HTMLInputElement>(null)
  const [query, setQuery] = useState<string | null>(null)
  const [open, setOpen] = useState(false)
  const [active, setActive] = useState(0)
  const chosen = options.find(option => option.value === value)?.label ?? selectedLabel ?? ''
  const needle = normalize(query ?? '')
  const matches = options.filter(option => normalize(`${option.label} ${option.description ?? ''}`).includes(needle))
  const current = Math.min(active, matches.length - 1)

  function close() { setOpen(false); setQuery(null) }
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
    <label htmlFor={id}>{tx(label)}</label>
    <div className="relative">
      <input ref={input} id={id} role="combobox" aria-expanded={open} aria-controls={`${id}-list`} aria-autocomplete="list" autoComplete="off"
        aria-activedescendant={open && matches[current] ? `${id}-${current}` : undefined}
        className={cn(filterControlClass, value && 'pr-11')} disabled={disabled} placeholder={placeholder && tx(placeholder)}
        value={query ?? chosen}
        onFocus={event => event.currentTarget.select()}
        onClick={() => setOpen(true)}
        onChange={event => { setQuery(event.target.value); setOpen(true); setActive(0) }}
        onBlur={close} onKeyDown={onKeyDown} />
      {value && !disabled && <button type="button" aria-label={tx("Seçimi temizle")} className="absolute inset-y-0 right-0 flex min-w-11 items-center justify-center rounded-r-lg text-text-muted hover:text-text"
        onClick={() => { onChange(''); close(); input.current?.focus() }}><X aria-hidden="true" className="size-4" /></button>}
    </div>
    {open && <ul id={`${id}-list`} role="listbox" aria-label={tx(label)} onMouseDown={event => event.preventDefault()}
      className="absolute z-20 max-h-64 w-full overflow-y-auto rounded-lg border border-border bg-surface py-1 font-normal text-text shadow-card">
      {matches.map((option, index) => <li key={option.value} id={`${id}-${index}`} role="option" aria-selected={option.value === value} onClick={() => pick(option)}
        className={cn('min-h-11 cursor-pointer px-3 py-2', index === current && 'bg-surface-3', option.value === value && 'font-medium')}>
        <span className="block break-words">{option.label}</span>
        {option.description && <span className="block break-words text-xs text-text-muted">{option.description}</span>}
      </li>)}
      {matches.length === 0 && <li role="presentation" className="px-3 py-2 text-text-muted">{tx("Eşleşen sonuç yok")}</li>}
    </ul>}
  </div>
}
