import { useEffect } from 'react'
import { Pagination } from '@/components/data/pagination'
import type { ListState } from './use-list-state'

/**
 * The pager under a list: the record count, with the page buttons once `total` rows need more than one page. `disabled` while a server page is loading (`query.isFetching`); `label` names the nav.
 * A page past the last one (a stale or hand-edited URL, rows deleted meanwhile) falls back to the last page while there are rows, so no list ever shows an empty page of a non-empty result.
 */
export function ListPager({ state, total, disabled, label }: { state: Pick<ListState, 'page' | 'pageSize' | 'setPage'>; total: number; disabled?: boolean; label?: string }) {
  const last = Math.max(1, Math.ceil(total / state.pageSize))
  const pastEnd = total > 0 && state.page > last
  const { setPage } = state
  useEffect(() => { if (pastEnd && !disabled) setPage(last, { replace: true }) }, [pastEnd, disabled, last, setPage])
  if (total === 0) return null
  return <Pagination label={label} page={Math.min(state.page, last)} pageSize={state.pageSize} totalCount={total} disabled={disabled} onPageChange={setPage} />
}
