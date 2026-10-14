import { useState, type ChangeEvent } from 'react'
import { DataTable, type Column } from '@/components/data/data-table'
import { Pagination } from '@/components/data/pagination'
import { Badge } from '@/components/ui/badge'
import { QueryState } from '@/components/data/query-state'
import { agentDate } from '@/features/agents/format'
import { inlineSelectClass } from '@/features/platform/styles'
import { useNotificationDeliveries } from './hooks'
import { deliveryStatusLabels, eventTypeLabels, kindLabels, phaseLabels, severityLabels } from './labels'
import { channelKinds, deliveryStatuses, eventTypes, type ChannelKind, type DeliveryFilters, type DeliveryStatus, type EventType, type NotificationDelivery } from './types'

const PAGE_SIZE = 25

const columns: Column<NotificationDelivery>[] = [
  { key: 'createdAt', header: 'Oluşturma', render: (row) => agentDate(row.createdAt) },
  { key: 'event', header: 'Olay', wrap: true, render: (row) => <div>{eventTypeLabels[row.eventType]} · {phaseLabels[row.phase]}<div className="text-xs text-text-muted">{severityLabels[row.severity]} · {row.subjectName}</div></div> },
  { key: 'channel', header: 'Kanal', wrap: true, render: (row) => <div>{row.channelName}<div className="text-xs text-text-muted">{kindLabels[row.channelKind]} · {row.targetDisplay}</div></div> },
  { key: 'status', header: 'Durum', render: (row) => { const meta = deliveryStatusLabels[row.status]; return <Badge tone={meta.tone}>{meta.label}</Badge> } },
  { key: 'error', header: 'Hata kodu', render: (row) => row.errorCode ? <code>{row.errorCode}</code> : '—' },
  { key: 'attempts', header: 'Deneme', render: (row) => row.attempts },
  { key: 'next', header: 'Sonraki deneme', render: (row) => row.nextAttemptAt ? agentDate(row.nextAttemptAt) : '—' },
]

export function DeliveriesSection() {
  const [page, setPage] = useState(1)
  const [status, setStatus] = useState<DeliveryStatus | ''>('')
  const [kind, setKind] = useState<ChannelKind | ''>('')
  const [eventType, setEventType] = useState<EventType | 'test' | ''>('')
  const filters: DeliveryFilters = { page, pageSize: PAGE_SIZE, status: status || undefined, kind: kind || undefined, eventType: eventType || undefined }
  const deliveries = useNotificationDeliveries(filters)
  const data = deliveries.data
  const filter = <T,>(set: (value: T) => void) => (event: ChangeEvent<HTMLSelectElement>) => { set(event.target.value as T); setPage(1) }

  return <section className="space-y-4 rounded-lg border border-border bg-surface p-4" aria-labelledby="notification-deliveries-heading">
    <div><h2 id="notification-deliveries-heading" className="font-medium text-text-heading">Gönderim geçmişi</h2>
      <p className="mt-1 text-sm text-text-muted">En yeni bildirim gönderimleri; silinen kanalların kayıtları korunur.</p></div>
    <div className="flex flex-wrap gap-3">
      <label className="text-sm text-text-muted">Durum<select className={inlineSelectClass} value={status} onChange={filter(setStatus)}><option value="">Tümü</option>{deliveryStatuses.map((item) => <option key={item} value={item}>{deliveryStatusLabels[item].label}</option>)}</select></label>
      <label className="text-sm text-text-muted">Kanal türü<select className={inlineSelectClass} value={kind} onChange={filter(setKind)}><option value="">Tümü</option>{channelKinds.map((item) => <option key={item} value={item}>{kindLabels[item]}</option>)}</select></label>
      <label className="text-sm text-text-muted">Olay türü<select className={inlineSelectClass} value={eventType} onChange={filter(setEventType)}><option value="">Tümü</option>{[...eventTypes, 'test' as const].map((item) => <option key={item} value={item}>{eventTypeLabels[item]}</option>)}</select></label>
    </div>
    <QueryState isPending={deliveries.isPending} error={deliveries.error}>
      {data && <>
        <DataTable rows={data.items} columns={columns} keyExtractor={(row) => row.id} emptyMessage="Gönderim kaydı bulunamadı." />
        <Pagination page={data.page} pageSize={data.pageSize} totalCount={data.totalCount} onPageChange={setPage} disabled={deliveries.isFetching} />
      </>}
    </QueryState>
  </section>
}
