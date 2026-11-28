import { useEffect, useRef, useState, type ReactNode } from 'react'
import { FilterField, Filters } from '@/components/ui/filters'
import { TextInput } from '@/components/ui/text-input'
import { tx, useLanguage } from '@/lib/i18n'
import type { ListState } from './use-list-state'

/** How long the search box waits after the last keystroke before it changes the URL (and, on a server list, asks the API). */
export const SEARCH_DEBOUNCE_MS = 300

/**
 * The search box keeps what is typed in its own state and writes it to the URL after a short pause or when the box loses focus.
 * The URL is read back only when something else changed it (clear, back/forward, a link), never as an echo of the typing, so fast typing cannot drop or reorder characters.
 */
function SearchInput({ value, onCommit, label, placeholder }: { value: string; onCommit: (value: string) => void; label: string; placeholder?: string }) {
  const [draft, setDraft] = useState(value)
  const [committed, setCommitted] = useState(value)
  const [seen, setSeen] = useState(value)
  const [resetVersion, setResetVersion] = useState(0)
  const timer = useRef<ReturnType<typeof setTimeout>>(undefined)
  if (value !== seen) {
    setSeen(value)
    if (value !== committed) { setCommitted(value); setDraft(value); setResetVersion(version => version + 1) }
  }
  const commit = (next: string) => { clearTimeout(timer.current); timer.current = undefined; if (next !== committed) { setCommitted(next); onCommit(next) } }
  useEffect(() => { clearTimeout(timer.current) }, [resetVersion])
  useEffect(() => () => clearTimeout(timer.current), [])
  return <TextInput type="search" aria-label={label} placeholder={placeholder} value={draft}
    onChange={event => { const next = event.target.value; setDraft(next); clearTimeout(timer.current); timer.current = setTimeout(() => commit(next), SEARCH_DEBOUNCE_MS) }}
    onBlur={() => commit(draft)} onKeyDown={event => { if (event.key === 'Enter') commit(draft) }} />
}

/**
 * The one toolbar above a list: the search box (when the state has one), your `FilterField`s as children, and "Filtreleri temizle" while filtering.
 * `label` names the region, `search.label` the search input. `total` (the matching rows) hides the clear button when nothing matches, because the empty list offers it.
 */
export function ListToolbar({ state, label, search, total, footer, children }: { state: Pick<ListState, 'searchable' | 'search' | 'setSearch' | 'filtering' | 'clear'>; label: string; search?: { label: string; placeholder: string }; total?: number; footer?: ReactNode; children?: ReactNode }) {
  useLanguage()
  return <Filters label={label} onClear={state.filtering && total !== 0 ? state.clear : undefined} footer={footer}>
    {state.searchable && search && <FilterField label={tx('Ara')}>
      <SearchInput value={state.search} onCommit={state.setSearch} label={search.label} placeholder={search.placeholder} />
    </FilterField>}
    {children}
  </Filters>
}
