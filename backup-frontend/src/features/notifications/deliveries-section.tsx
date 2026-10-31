import { tx, useLanguage } from '@/lib/i18n'
import type { ChangeEvent } from 'react'
import { Pagination } from '@/components/data/pagination'
import { Send } from 'lucide-react'
import { EmptyState } from '@/components/ui/empty-state'
import { Button } from '@/components/ui/button'
import { FilterField, Filters } from '@/components/ui/filters'
import { Fact, Facts, Section } from '@/components/ui/section'
import { Card } from '@/components/ui/card'
import { QueryState } from '@/components/data/query-state'
import { TechnicalDetails } from '@/components/ui/technical-details'
import { useNotificationDeliveries } from './hooks'
import { eventTypeLabels, kindLabels, phaseLabels, severityLabels } from './labels'
import { channelKinds, deliveryStatuses, visibleChannelKinds, visibleEventTypes } from './types'
import type { DeliveryFilters, NotificationDelivery } from './types'
import { LIST_PAGE_SIZE, oneOf, useListParams } from '@/features/platform/use-list-params'
import { formatDateTime, formatRelativeTime } from '@/lib/format'
import { StatusPill } from '@/components/data/status-pill'
import { statusLabel } from '@/lib/status'
import { Select } from '@/components/ui/text-input'

const eventFilters = () => [...visibleEventTypes(), 'break_glass_access', 'test'] as const

function DeliveryCard({ delivery }: { delivery: NotificationDelivery }) {
  const event = eventTypeLabels[delivery.eventType]
  return <li><Card as="article" aria-label={tx('{0} · {1}', { '0': event, '1': delivery.subjectName })} className="space-y-3">
    <div className="flex flex-wrap items-start justify-between gap-2">
      <h3 className="min-w-0 break-words font-medium text-text-heading">{event} · {delivery.subjectName}</h3>
      <StatusPill domain="delivery" status={delivery.status} />
    </div>
    <Facts>
      <Fact label={tx('Zaman')}><time dateTime={delivery.createdAt} title={formatDateTime(delivery.createdAt)}>{formatRelativeTime(delivery.createdAt)}</time></Fact>
      <Fact label={tx('Kanal')} hint={<span className="break-all">{kindLabels[delivery.channelKind]} · {delivery.targetDisplay}</span>}>{delivery.channelName}</Fact>
    </Facts>
    <TechnicalDetails items={[
      { label: tx('Gönderim kimliği'), value: delivery.id },
      { label: tx('Olay kimliği'), value: delivery.eventId },
      { label: tx('Olay türü'), value: delivery.eventType },
      { label: tx('Aşama'), value: phaseLabels[delivery.phase] },
      { label: tx('Önem düzeyi'), value: severityLabels[delivery.severity] },
      { label: tx('Deneme'), value: String(delivery.attempts) },
      ...(delivery.errorCode ? [{ label: tx('Hata kodu'), value: delivery.errorCode }] : []),
      ...(delivery.nextAttemptAt ? [{ label: tx('Sonraki deneme'), value: formatDateTime(delivery.nextAttemptAt) }] : []),
      ...(delivery.sentAt ? [{ label: tx('Gönderilme zamanı'), value: formatDateTime(delivery.sentAt) }] : []),
    ]} />
  </Card></li>
}

export function DeliveriesSection() {
  useLanguage()
  const { page, get, update } = useListParams()
  const status = oneOf(get('status'), deliveryStatuses)
  const kind = oneOf(get('kind'), channelKinds)
  const eventType = oneOf(get('eventType'), eventFilters())
  const filters: DeliveryFilters = { page, pageSize: LIST_PAGE_SIZE, status, kind, eventType }
  const deliveries = useNotificationDeliveries(filters)
  const data = deliveries.data
  const filter = (key: 'status' | 'kind' | 'eventType') => (event: ChangeEvent<HTMLSelectElement>) => update({ [key]: event.target.value })

  return <Section title={tx("Gönderim geçmişi")} description={tx("En yeni bildirim gönderimleri; silinen kanalların kayıtları korunur.")}>
    <div className="space-y-4">
      <Filters label={tx("Gönderim filtreleri")} onClear={status || kind || eventType ? () => update({ status: '', kind: '', eventType: '' }) : undefined}>
        <FilterField label={tx("Durum")}><Select aria-label={tx("Durum")} value={status ?? ''} onChange={filter('status')}><option value="">{tx("Tümü")}</option>{deliveryStatuses.map((item) => <option key={item} value={item}>{statusLabel('delivery', item)}</option>)}</Select></FilterField>
        <FilterField label={tx("Kanal türü")}><Select aria-label={tx("Kanal türü")} value={kind ?? ''} onChange={filter('kind')}><option value="">{tx("Tümü")}</option>{visibleChannelKinds().map((item) => <option key={item} value={item}>{kindLabels[item]}</option>)}</Select></FilterField>
        <FilterField label={tx("Olay türü")}><Select aria-label={tx("Olay türü")} value={eventType ?? ''} onChange={filter('eventType')}><option value="">{tx("Tümü")}</option>{eventFilters().map((item) => <option key={item} value={item}>{eventTypeLabels[item]}</option>)}</Select></FilterField>
      </Filters>
      <QueryState isPending={deliveries.isPending} error={deliveries.error}>
        {data && (data.totalCount === 0
          ? <EmptyState icon={Send} message={tx("Gönderim kaydı bulunamadı.")} action={<Button disabled={deliveries.isFetching} onClick={() => void deliveries.refetch()}>{tx('Yenile')}</Button>} />
          : <>
            <ul className="space-y-3">{data.items.map(delivery => <DeliveryCard key={delivery.id} delivery={delivery} />)}</ul>
            <Pagination page={data.page} pageSize={data.pageSize} totalCount={data.totalCount} onPageChange={next => update({ page: next })} disabled={deliveries.isFetching} />
          </>)}
      </QueryState>
    </div>
  </Section>
}
