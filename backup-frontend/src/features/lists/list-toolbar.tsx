import type { ReactNode } from 'react'
import { FilterField, Filters } from '@/components/ui/filters'
import { TextInput } from '@/components/ui/text-input'
import { tx, useLanguage } from '@/lib/i18n'
import type { ListState } from './use-list-state'

/**
 * The one toolbar above a list: the search box (when the state has one), your `FilterField`s as children, and "Filtreleri temizle" while filtering.
 * `label` names the region, `search.label` the search input. `total` (the matching rows) hides the clear button when nothing matches, because the empty list offers it.
 */
export function ListToolbar({ state, label, search, total, footer, children }: { state: Pick<ListState, 'searchable' | 'search' | 'setSearch' | 'filtering' | 'clear'>; label: string; search?: { label: string; placeholder?: string }; total?: number; footer?: ReactNode; children?: ReactNode }) {
  useLanguage()
  return <Filters label={label} onClear={state.filtering && total !== 0 ? state.clear : undefined} footer={footer}>
    {state.searchable && search && <FilterField label={tx('Ara')}>
      <TextInput type="search" aria-label={search.label} placeholder={search.placeholder} value={state.search} onChange={event => state.setSearch(event.target.value)} />
    </FilterField>}
    {children}
  </Filters>
}
