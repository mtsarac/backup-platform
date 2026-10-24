import { tx, useLanguage } from '@/lib/i18n'
import { DataList, type Column } from '@/components/data/data-list'
import { Pagination } from '@/components/data/pagination'
import { RelativeTime } from '@/components/data/relative-time'
import { Section } from '@/components/ui/section'
import { TechnicalDetails } from '@/components/ui/technical-details'
import { auditActionLabel } from '@/features/audit/action-labels'
import { auditDetailItems, auditResultLabel } from '@/features/audit/audit-details'
import { bareListClass } from '@/features/platform/list-empty'
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
    { key: 'at', header: tx('Zaman'), render: item => <RelativeTime value={item.at} /> },
    { key: 'action', card: 'title', header: tx('İşlem'), wrap: true, render: item => auditActionLabel(item.action) ?? tx('Bilinmeyen işlem') },
    { key: 'actor', header: tx('Yapan'), wrap: true, render: item => item.actorDisplayName ?? tx('Bilinmeyen kullanıcı') },
    { key: 'result', header: tx('Sonuç'), render: item => auditResultLabel(item.result) },
    { key: 'technical', card: 'actions', header: tx('Ayrıntılar'), wrap: true, render: item => <TechnicalDetails items={[
      { label: tx('İşlem kodu'), value: item.action },
      { label: tx('Hedef türü'), value: item.targetType },
      { label: tx('Hedef kimliği'), value: item.targetId },
      { label: tx('Kayıt kimliği'), value: item.id },
      { label: tx('Yapan kimliği'), value: item.actorUserId },
      { label: tx('Ham sonuç'), value: item.result ?? '—' },
      { label: tx('Tam zaman'), value: formatDateTime(item.at) },
      ...auditDetailItems(item.details),
    ]} /> },
  ]
  return <SupportQueryState isPending={query.isPending} error={query.error}>{query.data && <div className="space-y-4">
    <Section title={tx("Oturum audit kayıtları")} description={tx("Destek erişimi ve bu erişimle yapılan işlemler.")} flush className="overflow-hidden"><DataList caption={tx('Oturum audit kayıtları')} className={bareListClass} rows={query.data.items} columns={columns} keyExtractor={item => item.id} emptyMessage={tx("Bu oturum için audit kaydı yok.")} /></Section>
    <Pagination page={page} pageSize={query.data.pageSize} totalCount={query.data.totalCount} disabled={query.isFetching} onPageChange={next => update({ auditPage: next })} />
  </div>}</SupportQueryState>
}
