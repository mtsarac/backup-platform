import { useState, type FormEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { SortMenu } from '@/components/data/data-list'
import { QueryState } from '@/components/data/query-state'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { Field } from '@/components/ui/field'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { Section } from '@/components/ui/section'
import { TextInput } from '@/components/ui/text-input'
import { apiRequest } from '@/lib/api/client'
import { formatDateTime } from '@/lib/format'
import { tx, useLanguage } from '@/lib/i18n'
import { eventTypeLabels } from '@/features/notifications/labels'
import { ListEmpty } from '@/features/lists/list-empty'
import { ListPager } from '@/features/lists/list-pager'
import { ListToolbar } from '@/features/lists/list-toolbar'
import { toServerQuery } from '@/features/lists/server-list-query'
import { sortProps, useListState } from '@/features/lists/use-list-state'
import { failedDeliveryKeys, useFailedDeliveries } from './hooks'

interface Retention { retentionDays: number }

const listConfig = {
  search: true,
  sort: { first: { createdAt: 'desc', kind: 'asc', eventType: 'asc', channel: 'asc', attempts: 'desc' }, default: { key: 'createdAt', dir: 'desc' } },
  pageSize: 20,
} as const
const sortOptions = () => [
  { key: 'createdAt', label: tx('Zaman') }, { key: 'kind', label: tx('Kanal türü') }, { key: 'eventType', label: tx('Olay türü') },
  { key: 'channel', label: tx('Kanal') }, { key: 'attempts', label: tx('Deneme') },
]

export function NotificationAdministration() {
  useLanguage()
  const state = useListState(listConfig)
  const client = useQueryClient()
  const failed = useFailedDeliveries(toServerQuery(state))
  const retention = useQuery({ queryKey: ['platform', 'notification-history-retention'], queryFn: ({ signal }) => apiRequest<Retention>('platform/notifications/history-retention', { signal }) })
  const retry = useMutation({ mutationFn: (id: string) => apiRequest<void>(`platform/notifications/deliveries/${encodeURIComponent(id)}/retry`, { method: 'POST' }), onSuccess: () => client.invalidateQueries({ queryKey: failedDeliveryKeys.all }) })

  return <>
    <Section title={tx('Bildirim geçmişi saklama süresi')} description={tx('Bildirim geçmişi bu süreden sonra silinir; denetim geçmişi etkilenmez.')}>
      <QueryState isPending={retention.isPending} error={retention.error}>{retention.data && <RetentionForm key={retention.data.retentionDays} retention={retention.data} />}</QueryState>
    </Section>
    <Section title={tx('Başarısız bildirim gönderimleri')}>
      <div className="space-y-4">
        <ListToolbar state={state} label={tx('Başarısız gönderim filtreleri')} search={{ label: tx('Gönderim ara'), placeholder: tx('Kanal, hedef veya olay') }} total={failed.data?.totalCount} />
        <QueryState isPending={failed.isPending} error={failed.error}>
          {retry.error && <ErrorNotice error={retry.error} />}
          {failed.data && (failed.data.items.length === 0
            ? <ListEmpty message={tx("Başarısız bildirim gönderimi yok.")} filtering={state.filtering} onClear={state.clear} />
            : <>
              <SortMenu options={sortOptions()} sort={sortProps(state)} />
              <ul className="space-y-3">{failed.data.items.map((item) => <li key={item.id}><Card as="article" className="space-y-2">
                <h3 className="font-medium">{eventTypeLabels[item.eventType as keyof typeof eventTypeLabels] ?? item.eventType} · {item.subjectName}</h3>
                <p>{item.channelName} · {item.targetDisplay}</p>
                <p className="text-sm text-text-muted">{tx('Çalışma alanı')}: {item.tenantId} · {formatDateTime(item.createdAt)} · {tx('Deneme')}: {item.attempts}</p>
                {item.errorCode && <p className="text-sm text-text-muted">{tx('Hata kodu')}: {item.errorCode}</p>}
                <Button variant="outline" size="sm" disabled={retry.isPending} onClick={() => retry.mutate(item.id)}>{tx('Gönderimi yeniden dene')}</Button>
              </Card></li>)}</ul>
            </>)}
          {failed.data && <ListPager state={state} total={failed.data.totalCount} disabled={failed.isFetching} label={tx('Başarısız gönderim sayfaları')} />}
        </QueryState>
      </div>
    </Section>
  </>
}

function RetentionForm({ retention }: { retention: Retention }) {
  const [days, setDays] = useState(String(retention.retentionDays))
  const client = useQueryClient()
  const save = useMutation({ mutationFn: (retentionDays: number) => apiRequest<Retention>('platform/notifications/history-retention', { method: 'PUT', json: { retentionDays } }), onSuccess: (data) => { client.setQueryData(['platform', 'notification-history-retention'], data) } })
  const value = Number(days)
  const valid = /^\d+$/.test(days) && Number.isSafeInteger(value) && value >= 1 && value <= 36500
  function submit(event: FormEvent) { event.preventDefault(); if (valid) save.mutate(value) }
  return <form onSubmit={submit} className="space-y-3">
    <Field label={tx('Saklama süresi (gün)')} hint={tx('1 ile 36500 arasında bir tam sayı girin.')}>
      {(control) => <TextInput {...control} type="number" min={1} max={36500} step={1} required value={days} onChange={(event) => setDays(event.target.value)} />}
    </Field>
    {save.error && <ErrorNotice error={save.error} />}
    {save.isSuccess && <Notice tone="success">{tx('Saklama süresi kaydedildi.')}</Notice>}
    <Button type="submit" disabled={!valid || save.isPending}>{tx('Saklama süresini kaydet')}</Button>
  </form>
}
