import { Pagination } from '@/components/data/pagination'
import type { ListState } from './use-list-state'

/** The pager under a list: shown only when `total` rows need more than one page. `disabled` while a server page is loading (`query.isFetching`); `label` names the nav. */
export function ListPager({ state, total, disabled, label }: { state: Pick<ListState, 'page' | 'pageSize' | 'setPage'>; total: number; disabled?: boolean; label?: string }) {
  if (total <= state.pageSize) return null
  const page = Math.min(state.page, Math.ceil(total / state.pageSize))
  return <Pagination label={label} page={page} pageSize={state.pageSize} totalCount={total} disabled={disabled} onPageChange={state.setPage} />
}
