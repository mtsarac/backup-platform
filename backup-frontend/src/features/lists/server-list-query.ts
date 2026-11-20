import type { ListQueryParams } from '@/features/platform/types'
import type { FilterSpec, ListState } from './use-list-state'

/**
 * Wire contract of a server-paged list endpoint: query parameters `page`, `pageSize`, `search=<text>`, `sort=<field>`,
 * `dir=asc|desc` and one parameter per filter. Empty values are left out; `sort`/`dir` are left out while the order is the default.
 * The endpoint's `sort` allowlist uses the same field names as the list's sort keys.
 */
export type ServerListQuery<F extends Record<string, FilterSpec> = Record<string, FilterSpec>> =
  ListQueryParams & { page: number; pageSize: number } & { [N in keyof F]?: F[N] extends readonly (infer V)[] ? V : string }

/** The query object a feature `api.ts` puts into the URL. */
export function toServerQuery<K extends string, F extends Record<string, FilterSpec>>(state: ListState<K, F>): ServerListQuery<F> {
  const search = state.search.trim()
  return {
    page: state.page, pageSize: state.pageSize,
    ...(search && { search }),
    ...(!state.defaultSort && { sort: state.sort.key, dir: state.sort.dir }),
    ...Object.fromEntries(Object.entries(state.filters).filter(([, value]) => value)),
  } as ServerListQuery<F>
}
