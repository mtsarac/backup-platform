import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import type { ChangeEvent } from 'react'
import { Pagination } from '@/components/data/pagination'
import { Send } from 'lucide-react'
import { EmptyState } from '@/components/ui/empty-state'
import { Button } from '@/components/ui/button'
import { FilterField, Filters, filterControlClass } from '@/components/ui/filters'
import { Section } from '@/components/ui/section'
import { QueryState } from '@/components/data/query-state'
import { TechnicalDetails } from '@/components/ui/technical-details'
import { useNotificationDeliveries } from './hooks'
import { eventTypeLabels, kindLabels, phaseLabels, severityLabels } from './labels'
import { channelKinds, deliveryStatuses, eventTypes } from './types'
import type { ChannelKind, DeliveryFilters, DeliveryStatus, EventType, MandatoryEventType, NotificationDelivery } from './types'
import { formatDateTime, formatRelativeTime } from '@/lib/format'
import { StatusPill } from '@/components/data/status-pill'
import { statusLabel } from '@/lib/status'

const PAGE_SIZE = 25

function DeliveryCard({ delivery }: { delivery: NotificationDelivery }) {
  const event = eventTypeLabels[delivery.eventType]
  return <li><article aria-label={tx('{0} · {1}', { '0': event, '1': delivery.subjectName })} className="space-y-3 rounded-xl border border-border bg-surface p-4">
    <div className="flex flex-wrap items-start justify-between gap-2">
      <h3 className="min-w-0 break-words font-medium text-text-heading">{event} · {delivery.subjectName}</h3>
      <StatusPill domain="delivery" status={delivery.status} />
    </div>
    <dl className="grid gap-2 text-sm sm:grid-cols-2">
      <div><dt className="text-text-muted">{tx('Zaman')}</dt><dd><time dateTime={delivery.createdAt} title={formatDateTime(delivery.createdAt)}>{formatRelativeTime(delivery.createdAt)}</time></dd></div>
      <div><dt className="text-text-muted">{tx('Kanal')}</dt><dd className="break-words">{delivery.channelName}</dd><dd className="break-all text-text-muted">{kindLabels[delivery.channelKind]} · {delivery.targetDisplay}</dd></div>
    </dl>
    <TechnicalDetails items={[
      { label: tx('Gönderim kimliği'), value: delivery.id },
      { label: tx('Olay kimliği'), value: delivery.eventId },
      { label: tx('Olay türü'), value: delivery.eventType },
      { label: tx('Aşama'), value: phaseLabels[delivery.phase] },
      { label: tx('Önem düzeyi'), value: severityLabels[delivery.severity] },
      { label: tx('Deneme'), value: String(delivery.attempts) },
      { label: tx('Oluşturma'), value: formatDateTime(delivery.createdAt) },
      ...(delivery.errorCode ? [{ label: tx('Hata kodu'), value: delivery.errorCode }] : []),
      ...(delivery.nextAttemptAt ? [{ label: tx('Sonraki deneme'), value: formatDateTime(delivery.nextAttemptAt) }] : []),
      ...(delivery.sentAt ? [{ label: tx('Gönderilme zamanı'), value: formatDateTime(delivery.sentAt) }] : []),
    ]} />
  </article></li>
}

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
        <FilterField label={tx("Durum")}><select aria-label={tx("Durum")} className={`${filterControlClass} min-h-11`} value={status} onChange={filter(setStatus)}><option value="">{tx("Tümü")}</option>{deliveryStatuses.map((item) => <option key={item} value={item}>{statusLabel('delivery', item)}</option>)}</select></FilterField>
        <FilterField label={tx("Kanal türü")}><select aria-label={tx("Kanal türü")} className={`${filterControlClass} min-h-11`} value={kind} onChange={filter(setKind)}><option value="">{tx("Tümü")}</option>{channelKinds.map((item) => <option key={item} value={item}>{kindLabels[item]}</option>)}</select></FilterField>
        <FilterField label={tx("Olay türü")}><select aria-label={tx("Olay türü")} className={`${filterControlClass} min-h-11`} value={eventType} onChange={filter(setEventType)}><option value="">{tx("Tümü")}</option>{[...eventTypes, 'break_glass_access' as const, 'test' as const].map((item) => <option key={item} value={item}>{eventTypeLabels[item]}</option>)}</select></FilterField>
      </Filters>
      <QueryState isPending={deliveries.isPending} error={deliveries.error}>
        {data && (data.totalCount === 0
          ? <EmptyState icon={Send} message={tx("Gönderim kaydı bulunamadı.")} action={<Button disabled={deliveries.isFetching} onClick={() => void deliveries.refetch()}>{tx('Yenile')}</Button>} />
          : <>
            <ul className="space-y-3">{data.items.map(delivery => <DeliveryCard key={delivery.id} delivery={delivery} />)}</ul>
            <Pagination page={data.page} pageSize={data.pageSize} totalCount={data.totalCount} onPageChange={setPage} disabled={deliveries.isFetching} />
          </>)}
      </QueryState>
    </div>
  </Section>
}
