import { tx, useLanguage } from '@/lib/i18n'
import type { ChangeEvent } from 'react'
import { SortMenu } from '@/components/data/data-list'
import { Send } from 'lucide-react'
import { ListEmpty } from '@/features/lists/list-empty'
import { ListPager } from '@/features/lists/list-pager'
import { ListToolbar } from '@/features/lists/list-toolbar'
import { toServerQuery } from '@/features/lists/server-list-query'
import { sortProps, useListState } from '@/features/lists/use-list-state'
import { Button } from '@/components/ui/button'
import { FilterField } from '@/components/ui/filters'
import { Fact, Facts, Section } from '@/components/ui/section'
import { Card } from '@/components/ui/card'
import { QueryState } from '@/components/data/query-state'
import { TechnicalDetails } from '@/components/ui/technical-details'
import { useNotificationDeliveries } from './hooks'
import { eventTypeLabels, kindLabels, phaseLabels, severityLabels } from './labels'
import { channelKinds, deliveryStatuses, eventTypes, visibleChannelKinds, visibleEventTypes } from './types'
import type { NotificationDelivery } from './types'
import { formatDateTime, formatRelativeTime } from '@/lib/format'
import { StatusPill } from '@/components/data/status-pill'
import { statusLabel } from '@/lib/status'
import { Select } from '@/components/ui/text-input'

const listConfig = {
  search: true,
  filters: { status: deliveryStatuses, kind: channelKinds, eventType: [...eventTypes, 'test'] },
  sort: { first: { createdAt: 'desc', status: 'asc', kind: 'asc', eventType: 'asc', channel: 'asc', attempts: 'desc' }, default: { key: 'createdAt', dir: 'desc' } },
  pageKey: 'deliveries-page',
  paramPrefix: 'deliveries-',
} as const
const sortOptions = () => [
  { key: 'createdAt', label: tx('Zaman') }, { key: 'status', label: tx('Durum') }, { key: 'kind', label: tx('Kanal türü') },
  { key: 'eventType', label: tx('Olay türü') }, { key: 'channel', label: tx('Kanal') }, { key: 'attempts', label: tx('Deneme') },
]

function DeliveryCard({ delivery }: { delivery: NotificationDelivery }) {
  const event = eventTypeLabels[delivery.eventType] ?? delivery.eventType
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
  const state = useListState(listConfig)
  const deliveries = useNotificationDeliveries(toServerQuery(state))
  const data = deliveries.data
  const { status, kind, eventType } = state.filters
  const filter = (name: 'status' | 'kind' | 'eventType') => (event: ChangeEvent<HTMLSelectElement>) => state.setFilter(name, event.target.value)

  return <Section title={tx("Gönderim geçmişi")} description={tx("En yeni bildirim gönderimleri; silinen kanalların kayıtları korunur.")}>
    <div className="space-y-4">
      <ListToolbar state={state} label={tx("Gönderim filtreleri")} search={{ label: tx("Gönderim ara"), placeholder: tx("Kanal, hedef veya olay") }} total={data?.totalCount}>
        <FilterField label={tx("Durum")}><Select aria-label={tx("Durum")} value={status} onChange={filter('status')}><option value="">{tx("Tümü")}</option>{deliveryStatuses.map((item) => <option key={item} value={item}>{statusLabel('delivery', item)}</option>)}</Select></FilterField>
        <FilterField label={tx("Kanal türü")}><Select aria-label={tx("Kanal türü")} value={kind} onChange={filter('kind')}><option value="">{tx("Tümü")}</option>{visibleChannelKinds().map((item) => <option key={item} value={item}>{kindLabels[item]}</option>)}</Select></FilterField>
        <FilterField label={tx("Olay türü")}><Select aria-label={tx("Olay türü")} value={eventType} onChange={filter('eventType')}><option value="">{tx("Tümü")}</option>{[...visibleEventTypes(), 'test' as const].map((item) => <option key={item} value={item}>{eventTypeLabels[item]}</option>)}</Select></FilterField>
      </ListToolbar>
      <QueryState isPending={deliveries.isPending} error={deliveries.error}>
        {data && (data.items.length === 0
          ? <ListEmpty icon={Send} message={tx("Gönderim kaydı bulunamadı.")} filtering={state.filtering} onClear={state.clear} action={<Button disabled={deliveries.isFetching} onClick={() => void deliveries.refetch()}>{tx('Yenile')}</Button>} />
          : <>
            <SortMenu options={sortOptions()} sort={sortProps(state)} />
            <ul className="space-y-3">{data.items.map(delivery => <DeliveryCard key={delivery.id} delivery={delivery} />)}</ul>
          </>)}
        {data && <ListPager state={state} total={data.totalCount} disabled={deliveries.isFetching} label={tx("Gönderim sayfaları")} />}
      </QueryState>
    </div>
  </Section>
}
