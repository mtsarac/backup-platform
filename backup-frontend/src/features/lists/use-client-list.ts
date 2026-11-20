import { currentLocale } from '@/lib/i18n'
import type { ListState, FilterSpec } from './use-list-state'

interface ClientListOptions<T, K extends string, F extends Record<string, FilterSpec>> {
  /** The text the search matches (case-insensitive substring). */
  searchText?: (row: T) => string
  /** One per filter, called only while the filter is set. */
  filters?: { [N in keyof F]?: (row: T, value: string) => boolean }
  /** Ascending; see `compare.ts` for the missing-values-last rule. */
  comparators: Record<K, (a: T, b: T) => number>
  /** Orders rows the sort leaves equal, in either direction; without it they keep the order of `rows`. */
  tieBreak?: (a: T, b: T) => number
  /** Rows per page; leave out for no paging. */
  pageSize?: number
}

/** Search, filter, sort and page a list that arrives complete. Returns the visible page, the matching count and the (clamped) page. */
export function useClientList<T, K extends string, F extends Record<string, FilterSpec>>(rows: readonly T[], state: ListState<K, F>, options: ClientListOptions<T, K, F>) {
  const needle = state.search.trim().toLocaleLowerCase(currentLocale())
  const active = Object.entries(state.filters).filter(([, value]) => value) as [keyof F & string, string][]
  const compare = options.comparators[state.sort.key]
  const matching = rows.filter(row => (!needle || options.searchText?.(row).toLocaleLowerCase(currentLocale()).includes(needle))
    && active.every(([name, value]) => options.filters?.[name]?.(row, value) ?? true))
  const sorted = matching.toSorted((a, b) => { const order = compare(a, b); return (state.sort.dir === 'asc' || !Number.isFinite(order) ? order : -order) || (options.tieBreak?.(a, b) ?? 0) })
  const { pageSize } = options
  const pageCount = pageSize ? Math.max(1, Math.ceil(sorted.length / pageSize)) : 1
  const page = Math.min(state.page, pageCount)
  return { rows: pageSize ? sorted.slice((page - 1) * pageSize, page * pageSize) : sorted, total: sorted.length, page, pageCount }
}
