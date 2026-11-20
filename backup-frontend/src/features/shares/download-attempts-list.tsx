import { Share2 } from 'lucide-react'
import { DataList, type Column } from '@/components/data/data-list'
import { QueryState } from '@/components/data/query-state'
import { StatusPill } from '@/components/data/status-pill'
import { Button } from '@/components/ui/button'
import { FilterField } from '@/components/ui/filters'
import { ErrorNotice } from '@/components/ui/notice'
import { Select, TextInput } from '@/components/ui/text-input'
import { ListEmpty } from '@/features/lists/list-empty'
import { ListPager } from '@/features/lists/list-pager'
import { ListToolbar } from '@/features/lists/list-toolbar'
import { toServerQuery } from '@/features/lists/server-list-query'
import { sortProps, useListState } from '@/features/lists/use-list-state'
import { describeFailure, platformOnlyMessage } from '@/lib/api/failure-messages'
import { formatBytes, formatDateTime } from '@/lib/format'
import { tx, useLanguage } from '@/lib/i18n'
import { downloadAttemptsListConfig } from './download-attempts-config'
import { useDownloadAttempts } from './hooks'
import type { DownloadAttemptScope, DownloadAttemptView } from './types'

/** The download history: searched, filtered, sorted and paged by the server. The platform history also filters by share, backup file and user id. */
export function DownloadAttemptsList({ scope }: { scope: DownloadAttemptScope }) {
  useLanguage()
  const platform = scope.route === 'platform'
  const state = useListState(downloadAttemptsListConfig)
  const query = useDownloadAttempts(scope, toServerQuery(state))

  const columns: Column<DownloadAttemptView>[] = [
    ...(platform ? [{ key: 'tenant', header: tx('Tenant'), sortable: true, render: (attempt: DownloadAttemptView) => attempt.tenantName || attempt.tenantId }] : []),
    { key: 'artifact', header: tx('Yedek dosyası'), sortable: true, card: 'title', render: attempt => attempt.artifactName || attempt.artifactId },
    { key: 'share', header: tx('Paylaşım'), render: attempt => attempt.shareId ? <span title={attempt.shareId}>#{attempt.shareId.slice(0, 8)}</span> : '—' },
    { key: 'user', header: tx('Kullanıcı'), sortable: true, render: attempt => attempt.initiatingUserDisplayName || attempt.initiatingUserId },
    { key: 'startedAt', header: tx('Başlangıç'), sortable: true, render: attempt => formatDateTime(attempt.startedAtUtc) },
    { key: 'endedAt', header: tx('Bitiş'), sortable: true, render: attempt => (attempt.endedAtUtc ? formatDateTime(attempt.endedAtUtc) : '—') },
    { key: 'result', header: tx('Sonuç'), sortable: true, card: 'status', render: attempt => <StatusPill domain="downloadAttempt" status={attempt.result} /> },
    { key: 'bytes', header: tx('Aktarılan veri'), sortable: true, render: attempt => formatBytes(attempt.bytesTransferred) },
    {
      key: 'failureReason',
      header: tx('Hata / Red gerekçesi'),
      wrap: true,
      render: attempt => {
        if (!attempt.failureReason) return '—'
        const failure = describeFailure(attempt.failureReason)
        return <ErrorNotice {...failure} technical={failure.technical.length ? failure.technical : [{ label: tx('Sunucu mesajı'), value: attempt.failureReason }]} />
      },
    },
  ]

  return <QueryState forbiddenMessage={platform ? platformOnlyMessage : undefined} isPending={query.isPending} error={query.error}>
    {query.data && <div className="space-y-4">
      <ListToolbar state={state} label={tx('İndirme girişimi filtreleri')} total={query.data.totalCount} search={{ label: tx('İndirme girişimi ara'), placeholder: tx('Yedek dosyası, kullanıcı veya sonuç') }}>
        <FilterField label={tx('Sonuç')}>
          <Select aria-label={tx('Sonuç')} value={state.filters.result} onChange={event => state.setFilter('result', event.target.value)}>
            <option value="">{tx('Tümü')}</option>
            <option value="Completed">{tx('Tamamlandı')}</option>
            <option value="Failed">{tx('Başarısız')}</option>
            <option value="Cancelled">{tx('İptal edildi')}</option>
            <option value="Refused">{tx('Reddedildi')}</option>
          </Select>
        </FilterField>
        {platform && <>
          <FilterField label={tx('Paylaşım')}><TextInput aria-label={tx('Paylaşım ID')} value={state.filters.shareId} onChange={event => state.setFilter('shareId', event.target.value)} /></FilterField>
          <FilterField label={tx('Yedek dosyası')}><TextInput aria-label={tx('Yedek dosyası ID')} value={state.filters.artifactId} onChange={event => state.setFilter('artifactId', event.target.value)} /></FilterField>
          <FilterField label={tx('Kullanıcı')}><TextInput aria-label={tx('Kullanıcı ID')} value={state.filters.userId} onChange={event => state.setFilter('userId', event.target.value)} /></FilterField>
        </>}
      </ListToolbar>
      {query.data.totalCount === 0 && !state.filtering
        ? <ListEmpty icon={Share2} message={tx('Henüz indirme girişimi kaydı yok.')} action={<Button onClick={() => void query.refetch()}>{tx('Yenile')}</Button>} />
        : <DataList caption={tx('İndirme girişimleri')} rows={query.data.items} columns={columns} keyExtractor={attempt => attempt.id} sort={sortProps(state)}
          emptyMessage={tx('Aramanızla eşleşen indirme girişimi yok.')} onClearFilters={state.filtering ? state.clear : undefined} />}
      <ListPager state={state} total={query.data.totalCount} disabled={query.isFetching} label={tx('İndirme girişimi sayfaları')} />
    </div>}
  </QueryState>
}
