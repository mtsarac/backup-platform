import { DataTable, type Column } from '@/components/data/data-table'
import { Pagination } from '@/components/data/pagination'
import { artifactDate } from '@/features/artifacts/format'
import { PLATFORM_PAGE_SIZE, useListParams } from '@/features/platform/use-list-params'
import { useSupportAudit } from './hooks'
import { SupportQueryState } from './support-query-state'
import type { SupportAuditEntry, SupportSession } from './types'

export function SupportAuditTab({ session }: { session: SupportSession }) {
  const { page, update } = useListParams('auditPage')
  const query = useSupportAudit(session, { page, pageSize: PLATFORM_PAGE_SIZE })
  const columns: Column<SupportAuditEntry>[] = [
    { key: 'at', header: 'Zaman', render: item => artifactDate(item.at) },
    { key: 'action', header: 'İşlem', wrap: true, render: item => item.action },
    { key: 'actor', header: 'Yapan', wrap: true, render: item => item.actorDisplayName ?? item.actorUserId },
    { key: 'target', header: 'Hedef', wrap: true, render: item => <div>{item.targetType}<p className="text-xs text-text-muted">{item.targetId}</p></div> },
    { key: 'result', header: 'Sonuç', render: item => item.result ?? '—' },
  ]
  return <SupportQueryState isPending={query.isPending} error={query.error}>{query.data && <div className="space-y-4"><DataTable rows={query.data.items} columns={columns} keyExtractor={item => item.id} emptyMessage="Bu oturum için audit kaydı yok." /><Pagination page={page} pageSize={query.data.pageSize} totalCount={query.data.totalCount} disabled={query.isFetching} onPageChange={next => update({ auditPage: next })} /></div>}</SupportQueryState>
}
