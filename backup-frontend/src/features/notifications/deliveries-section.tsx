import { tx, useLanguage } from '@/lib/i18n'
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
import type { ChannelKind, DeliveryFilters, DeliveryStatus, EventType, MandatoryEventType, NotificationDelivery } from './types'

const PAGE_SIZE = 25

const columns: Column<NotificationDelivery>[] = [
  { key: 'createdAt', get header() { return tx("Oluşturma") }, render: (row) => agentDate(row.createdAt) },
  { key: 'event', get header() { return tx("Olay") }, wrap: true, render: (row) => <div>{eventTypeLabels[row.eventType]} · {phaseLabels[row.phase]}<div className="text-xs text-text-muted">{severityLabels[row.severity]} · {row.subjectName}</div></div> },
  { key: 'channel', get header() { return tx("Kanal") }, wrap: true, render: (row) => <div>{row.channelName}<div className="text-xs text-text-muted">{kindLabels[row.channelKind]} · {row.targetDisplay}</div></div> },
  { key: 'status', get header() { return tx("Durum") }, render: (row) => { const meta = deliveryStatusLabels[row.status]; return <div className="flex flex-col items-start gap-1"><Badge tone={meta.tone} dot>{tx(meta.label)}</Badge>{row.errorCode && <code className="break-all text-sm text-text-muted">{row.errorCode}</code>}</div> } },
  { key: 'attempts', get header() { return tx("Deneme") }, render: (row) => row.attempts },
  { key: 'next', get header() { return tx("Sonraki deneme") }, render: (row) => row.nextAttemptAt ? agentDate(row.nextAttemptAt) : '—' },
]

export function DeliveriesSection() {
  useLanguage()
  const [page, setPage] = useState(1)
  const [status, setStatus] = useState<DeliveryStatus | ''>('')
  const [kind, setKind] = useState<ChannelKind | ''>('')
  const [eventType, setEventType] = useState<EventType | MandatoryEventType | 'test' | ''>('')
  const filters: DeliveryFilters = { page, pageSize: PAGE_SIZE, status: status || undefined, kind: kind || undefined, eventType: eventType || undefined }
  const deliveries = useNotificationDeliveries(filters)
  const data = deliveries.data
  const filter = <T,>(set: (value: T) => void) => (event: ChangeEvent<HTMLSelectElement>) => { set(event.target.value as T); setPage(1) }

  return <Section title={tx("Gönderim geçmişi")} description={tx("En yeni bildirim gönderimleri; silinen kanalların kayıtları korunur.")}>
    <div className="space-y-4">
      <Filters label={tx("Gönderim filtreleri")}>
        <FilterField label={tx("Durum")}><select aria-label={tx("Durum")} className={filterControlClass} value={status} onChange={filter(setStatus)}><option value="">{tx("Tümü")}</option>{deliveryStatuses.map((item) => <option key={item} value={item}>{tx(deliveryStatusLabels[item].label)}</option>)}</select></FilterField>
        <FilterField label={tx("Kanal türü")}><select aria-label={tx("Kanal türü")} className={filterControlClass} value={kind} onChange={filter(setKind)}><option value="">{tx("Tümü")}</option>{channelKinds.map((item) => <option key={item} value={item}>{kindLabels[item]}</option>)}</select></FilterField>
        <FilterField label={tx("Olay türü")}><select aria-label={tx("Olay türü")} className={filterControlClass} value={eventType} onChange={filter(setEventType)}><option value="">{tx("Tümü")}</option>{[...eventTypes, 'break_glass_access' as const, 'test' as const].map((item) => <option key={item} value={item}>{eventTypeLabels[item]}</option>)}</select></FilterField>
      </Filters>
      <QueryState isPending={deliveries.isPending} error={deliveries.error}>
        {data && (data.totalCount === 0
          ? <div className="rounded-xl border border-border bg-surface"><EmptyState icon={Send} title={tx("Gönderim kaydı bulunamadı.")}>{tx("Bildirim gönderildiğinde kayıtlar burada listelenir.")}</EmptyState></div>
          : <>
            <DataTable rows={data.items} columns={columns} keyExtractor={(row) => row.id} />
            <Pagination page={data.page} pageSize={data.pageSize} totalCount={data.totalCount} onPageChange={setPage} disabled={deliveries.isFetching} />
          </>)}
      </QueryState>
    </div>
  </Section>
}
