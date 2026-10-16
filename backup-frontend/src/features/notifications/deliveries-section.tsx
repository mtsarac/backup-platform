import { useState } from 'react'
import type { ChangeEvent } from 'react'
import { DataTable } from '@/components/data/data-table'
import type { Column } from '@/components/data/data-table'
import { Pagination } from '@/components/data/pagination'
import { Send } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { EmptyState } from '@/components/ui/empty-state'
import { FilterField, Filters, filterControlClass } from '@/components/ui/filters'
import { Section } from '@/components/ui/section'
import { QueryState } from '@/components/data/query-state'
import { agentDate } from '@/features/agents/format'
import { useNotificationDeliveries } from './hooks'
import { deliveryStatusLabels, eventTypeLabels, kindLabels, phaseLabels, severityLabels } from './labels'
import { channelKinds, deliveryStatuses, eventTypes } from './types'
import type { ChannelKind, DeliveryFilters, DeliveryStatus, EventType, NotificationDelivery } from './types'

const PAGE_SIZE = 25

const columns: Column<NotificationDelivery>[] = [
  { key: 'createdAt', header: 'Oluşturma', render: (row) => agentDate(row.createdAt) },
  { key: 'event', header: 'Olay', wrap: true, render: (row) => <div>{eventTypeLabels[row.eventType]} · {phaseLabels[row.phase]}<div className="text-xs text-text-muted">{severityLabels[row.severity]} · {row.subjectName}</div></div> },
  { key: 'channel', header: 'Kanal', wrap: true, render: (row) => <div>{row.channelName}<div className="text-xs text-text-muted">{kindLabels[row.channelKind]} · {row.targetDisplay}</div></div> },
  { key: 'status', header: 'Durum', render: (row) => { const meta = deliveryStatusLabels[row.status]; return <div className="flex flex-col items-start gap-1"><Badge tone={meta.tone} dot>{meta.label}</Badge>{row.errorCode && <code className="break-all text-sm text-text-muted">{row.errorCode}</code>}</div> } },
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

  return <Section title="Gönderim geçmişi" description="En yeni bildirim gönderimleri; silinen kanalların kayıtları korunur.">
    <div className="space-y-4">
      <Filters label="Gönderim filtreleri">
        <FilterField label="Durum"><select aria-label="Durum" className={filterControlClass} value={status} onChange={filter(setStatus)}><option value="">Tümü</option>{deliveryStatuses.map((item) => <option key={item} value={item}>{deliveryStatusLabels[item].label}</option>)}</select></FilterField>
        <FilterField label="Kanal türü"><select aria-label="Kanal türü" className={filterControlClass} value={kind} onChange={filter(setKind)}><option value="">Tümü</option>{channelKinds.map((item) => <option key={item} value={item}>{kindLabels[item]}</option>)}</select></FilterField>
        <FilterField label="Olay türü"><select aria-label="Olay türü" className={filterControlClass} value={eventType} onChange={filter(setEventType)}><option value="">Tümü</option>{[...eventTypes, 'test' as const].map((item) => <option key={item} value={item}>{eventTypeLabels[item]}</option>)}</select></FilterField>
      </Filters>
      <QueryState isPending={deliveries.isPending} error={deliveries.error}>
        {data && (data.totalCount === 0
          ? <div className="rounded-xl border border-border bg-surface"><EmptyState icon={Send} title="Gönderim kaydı bulunamadı.">Bildirim gönderildiğinde kayıtlar burada listelenir.</EmptyState></div>
          : <>
            <DataTable rows={data.items} columns={columns} keyExtractor={(row) => row.id} />
            <Pagination page={data.page} pageSize={data.pageSize} totalCount={data.totalCount} onPageChange={setPage} disabled={deliveries.isFetching} />
          </>)}
      </QueryState>
    </div>
  </Section>
}
