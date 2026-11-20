/**
 * The one URL-backed state of a list (search, filters, sort, page). Declare it once, outside the component:
 *
 *   const config = {
 *     search: true,
 *     filters: { status: ['ok', 'failed'], from: 'date', owner: 'text' },   // allowed values | 'date' | 'text'
 *     sort: { first: { name: 'asc', created: 'desc' }, default: { key: 'created', dir: 'desc' } },
 *   } as const
 *
 * Client-side list (the API returns everything):
 *   const state = useListState(config)
 *   const list = useClientList(rows, state, { searchText: r => r.name, filters: { status: (r, v) => r.status === v },
 *     comparators: { name: (a, b) => compareText(a.name, b.name), created: (a, b) => compareDate(a.createdAt, b.createdAt) }, pageSize: state.pageSize })
 *   <ListToolbar state={state} label="..." search={{ label: '...' }}>{filter fields}</ListToolbar>
 *   <DataList rows={list.rows} sort={sortProps(state)} columns={[{ key: 'name', sortable: true, ... }]} onClearFilters={state.filtering ? state.clear : undefined} />
 *   <Pagination page={list.page} pageSize={state.pageSize} totalCount={list.total} onPageChange={state.setPage} />   // when list.total > state.pageSize
 *
 * Server-paged list (the endpoint filters, sorts and pages): `useThings(toServerQuery(state))`, see server-list-query.ts.
 *
 * Any change but paging goes back to page 1; unknown values in the URL are ignored; typing in the search box replaces the history entry.
 */
import { useListParams, oneOf, dayParam, LIST_PAGE_SIZE } from '@/features/platform/use-list-params'
import type { SortDirection, SortState } from '@/components/data/data-list'

/** What a filter accepts: a closed set of values (`as const`), a `YYYY-MM-DD` day, or free text. */
export type FilterSpec = readonly string[] | 'date' | 'text'
export interface ListConfig<K extends string = string, F extends Record<string, FilterSpec> = Record<string, FilterSpec>> {
  /** The `search` URL parameter. */
  search?: boolean
  filters?: F
  sort: {
    /** Every sortable key and the direction it sorts in when chosen first. */
    first: Record<K, SortDirection>
    /** The order with no `sort`/`dir` in the URL (they are left out of the URL when equal to this). */
    default: { key: K; dir: SortDirection }
  }
  /** Rows per page, default 25. */
  pageSize?: number
  /** The page URL parameter, for a page with two lists. */
  pageKey?: string
}
type FilterValues<F> = { [N in keyof F]: F[N] extends readonly (infer V)[] ? V | '' : string }

export interface ListState<K extends string = string, F extends Record<string, FilterSpec> = Record<string, FilterSpec>> {
  search: string
  /** '' when a filter is off. */
  filters: FilterValues<F>
  sort: { key: K; dir: SortDirection }
  page: number
  pageSize: number
  /** Whether the config declares a search box. */
  searchable: boolean
  /** The directions the sortable keys start in. */
  first: Record<K, SortDirection>
  /** True while the sort is the configured default (the URL carries no `sort`). */
  defaultSort: boolean
  /** A search or any filter is set. */
  filtering: boolean
  setSearch: (value: string) => void
  setFilter: (name: keyof F & string, value: string) => void
  setSort: (key: K, dir: SortDirection) => void
  /** Active key: flips the direction; another key: its first direction. */
  toggleSort: (key: K) => void
  setPage: (page: number) => void
  /** Empties the search and every filter (the sort stays). */
  clear: () => void
}

/** Reads the list state out of URL values; anything the config does not allow is dropped. */
export function parseListState<K extends string, F extends Record<string, FilterSpec>>(get: (name: string) => string, config: ListConfig<K, F>) {
  const filters = Object.fromEntries(Object.entries(config.filters ?? {}).map(([name, spec]) => {
    const value = get(name)
    return [name, spec === 'text' ? value : spec === 'date' ? dayParam(value) : oneOf(value, spec) ?? '']
  })) as FilterValues<F>
  const key = oneOf(get('sort'), Object.keys(config.sort.first) as K[])
  const dir = oneOf(get('dir'), ['asc', 'desc'] as const) ?? (key ? config.sort.first[key] : config.sort.default.dir)
  return { search: config.search ? get('search') : '', filters, sort: { key: key ?? config.sort.default.key, dir } }
}

export function useListState<const F extends Record<string, FilterSpec> = Record<never, FilterSpec>, K extends string = string>(config: ListConfig<K, F>): ListState<K, F> {
  const { page, get, update } = useListParams(config.pageKey)
  const { search, filters, sort } = parseListState(get, config)
  const names = Object.keys(config.filters ?? {})
  const isDefault = (key: K, dir: SortDirection) => key === config.sort.default.key && dir === config.sort.default.dir
  const setSort = (key: K, dir: SortDirection) => update(isDefault(key, dir) ? { sort: '', dir: '' } : { sort: key, dir })
  return {
    search, filters, sort, page, setSort,
    pageSize: config.pageSize ?? LIST_PAGE_SIZE,
    searchable: Boolean(config.search),
    first: config.sort.first,
    defaultSort: isDefault(sort.key, sort.dir),
    filtering: Boolean(search.trim() || Object.values(filters).some(Boolean)),
    setSearch: value => update({ search: value }, { replace: true }),
    setFilter: (name, value) => update({ [name]: value }),
    toggleSort: key => setSort(key, key === sort.key ? (sort.dir === 'asc' ? 'desc' : 'asc') : config.sort.first[key]),
    setPage: next => update({ [config.pageKey ?? 'page']: next }),
    clear: () => update(Object.fromEntries([...(config.search ? ['search'] : []), ...names].map(name => [name, '']))),
  }
}

/** The `sort` prop of `DataList` for this state. */
export function sortProps<K extends string>(state: Pick<ListState<K>, 'sort' | 'first' | 'setSort'>): SortState {
  return { key: state.sort.key, direction: state.sort.dir, first: state.first, onChange: (key, dir) => state.setSort(key as K, dir) }
}
