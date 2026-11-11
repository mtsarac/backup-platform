import { useState, type FormEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Pagination } from '@/components/data/pagination'
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

interface FailedDelivery {
  id: string
  tenantId: string
  eventType: string
  subjectName: string
  channelName: string
  targetDisplay: string
  attempts: number
  errorCode: string | null
  createdAt: string
}
interface FailedPage { items: FailedDelivery[]; totalCount: number; page: number; pageSize: number }
interface Retention { retentionDays: number }

export function NotificationAdministration() {
  useLanguage()
  const [page, setPage] = useState(1)
  const client = useQueryClient()
  const failed = useQuery({ queryKey: ['platform', 'failed-notification-deliveries', page], queryFn: ({ signal }) => apiRequest<FailedPage>(`platform/notifications/deliveries/failed?page=${page}&pageSize=20`, { signal }) })
  const retention = useQuery({ queryKey: ['platform', 'notification-history-retention'], queryFn: ({ signal }) => apiRequest<Retention>('platform/notifications/history-retention', { signal }) })
  const retry = useMutation({ mutationFn: (id: string) => apiRequest<void>(`platform/notifications/deliveries/${encodeURIComponent(id)}/retry`, { method: 'POST' }), onSuccess: () => client.invalidateQueries({ queryKey: ['platform', 'failed-notification-deliveries'] }) })

  return <>
    <Section title={tx('Bildirim geçmişi saklama süresi')} description={tx('Bildirim geçmişi bu süreden sonra silinir; denetim geçmişi etkilenmez.')}>
      <QueryState isPending={retention.isPending} error={retention.error}>{retention.data && <RetentionForm key={retention.data.retentionDays} retention={retention.data} />}</QueryState>
    </Section>
    <Section title={tx('Başarısız bildirim gönderimleri')}>
      <QueryState isPending={failed.isPending} error={failed.error} empty={failed.data?.totalCount === 0}>
        {retry.error && <ErrorNotice error={retry.error} />}
        <ul className="space-y-3">{failed.data?.items.map((item) => <li key={item.id}><Card as="article" className="space-y-2">
          <h3 className="font-medium">{eventTypeLabels[item.eventType as keyof typeof eventTypeLabels] ?? item.eventType} · {item.subjectName}</h3>
          <p>{item.channelName} · {item.targetDisplay}</p>
          <p className="text-sm text-text-muted">{tx('Çalışma alanı')}: {item.tenantId} · {formatDateTime(item.createdAt)} · {tx('Deneme')}: {item.attempts}</p>
          {item.errorCode && <p className="text-sm text-text-muted">{tx('Hata kodu')}: {item.errorCode}</p>}
          <Button variant="outline" size="sm" disabled={retry.isPending} onClick={() => retry.mutate(item.id)}>{tx('Gönderimi yeniden dene')}</Button>
        </Card></li>)}</ul>
        {failed.data && <Pagination page={failed.data.page} pageSize={failed.data.pageSize} totalCount={failed.data.totalCount} onPageChange={setPage} disabled={failed.isFetching} />}
      </QueryState>
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
