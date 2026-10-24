import { tx, useLanguage } from '@/lib/i18n'
import { DataTable, type Column } from '@/components/data/data-table'
import { Pagination } from '@/components/data/pagination'
import { Section } from '@/components/ui/section'
import { bareTableClass } from '@/features/platform/list-empty'
import { PLATFORM_PAGE_SIZE, useListParams } from '@/features/platform/use-list-params'
import { useSupportAudit } from './hooks'
import { SupportQueryState } from './support-query-state'
import type { SupportAuditEntry, SupportSession } from './types'
import { formatDateTime } from '@/lib/format'

export function SupportAuditTab({ session }: { session: SupportSession }) {
  useLanguage()
  const { page, update } = useListParams('auditPage')
  const query = useSupportAudit(session, { page, pageSize: PLATFORM_PAGE_SIZE })
  const columns: Column<SupportAuditEntry>[] = [
    { key: 'at', header: tx('Zaman'), render: item => formatDateTime(item.at) },
    { key: 'action', header: tx('İşlem'), wrap: true, render: item => item.action },
    { key: 'actor', header: tx('Yapan'), wrap: true, render: item => item.actorDisplayName ?? item.actorUserId },
    { key: 'target', header: tx('Hedef'), wrap: true, render: item => <div>{item.targetType}<p className="break-all font-mono text-xs text-text-muted">{item.targetId}</p></div> },
    { key: 'result', header: tx('Sonuç'), render: item => item.result ?? '—' },
  ]
  return <SupportQueryState isPending={query.isPending} error={query.error}>{query.data && <div className="space-y-4">
    <Section title={tx("Oturum audit kayıtları")} description={tx("Destek erişimi ve bu erişimle yapılan işlemler.")} flush className="overflow-hidden"><DataTable className={bareTableClass} rows={query.data.items} columns={columns} keyExtractor={item => item.id} emptyMessage={tx("Bu oturum için audit kaydı yok.")} /></Section>
    <Pagination page={page} pageSize={query.data.pageSize} totalCount={query.data.totalCount} disabled={query.isFetching} onPageChange={next => update({ auditPage: next })} />
  </div>}</SupportQueryState>
}
