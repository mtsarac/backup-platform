import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { ListPager } from '@/features/lists/list-pager'
import { ListToolbar } from '@/features/lists/list-toolbar'
import { toServerQuery } from '@/features/lists/server-list-query'
import { useListState } from '@/features/lists/use-list-state'
import { errorMessage } from '@/lib/api/failure-messages'
import { usePlatformConnections, usePlatformProviders } from './hooks'
import { connectionOptions } from './job-connection-picker-utils'
import { CheckField } from '@/components/ui/check-field'

const listConfig = { search: true, sort: { first: { createdAt: 'desc' }, default: { key: 'createdAt', dir: 'desc' } }, pageSize: 20, pageKey: 'connections-page', paramPrefix: 'connections-' } as const

/** Quotas account for existing replicas, including disabled connections. Catalog refreshes never remove selected IDs. */
export function QuotaConnectionPicker({ values, onChange, invalid }: { values: readonly string[]; onChange: (values: string[]) => void; invalid?: boolean }) {
  useLanguage()
  const state = useListState(listConfig)
  const [selectedLabels, setSelectedLabels] = useState<Record<string, string>>({})
  const connections = usePlatformConnections(toServerQuery(state))
  const providers = usePlatformProviders()
  const options = connectionOptions(connections.data?.items ?? [], providers.data?.items ?? [], true)
  const error = connections.error || providers.error

  function toggle(id: string, label: string) {
    setSelectedLabels((current) => ({ ...current, [id]: label }))
    onChange(values.includes(id) ? values.filter((value) => value !== id) : [...values, id])
  }

  return <div className="space-y-2">
    <ListToolbar state={state} label={tx("Depolama bağlantıları filtreleri")} search={{ label: tx("Tenant veya bağlantı adı ara"), placeholder: tx("Tenant veya bağlantı adı") }} />
    <fieldset className="space-y-2" aria-invalid={invalid}>
      <legend className="text-xs text-text-muted">{tx("Depolama bağlantıları listesi")}</legend>
      {values.map((id, index) => {
        const option = options.find((item) => item.id === id)
        return <CheckField key={id} checked onChange={() => onChange(values.filter((value) => value !== id))} label={option?.label ?? selectedLabels[id] ?? tx("Mevcut bağlantı {0} · Bu listede bulunamadı; seçim korunuyor", { "0": index + 1 })} />
      })}
      {options.filter((option) => !values.includes(option.id)).map((option) => <CheckField key={option.id} checked={false} disabled={connections.isFetching || providers.isPending || Boolean(error)} onChange={() => toggle(option.id, option.label)} label={tx(option.label)} />)}
    </fieldset>
    {connections.isFetching && <p role="status" className="text-xs text-text-muted">{tx("Bağlantılar yükleniyor…")}</p>}
    {error ? <p role="status" className="text-xs text-error">{errorMessage(error)} <Button variant="link" size="sm" type="button" onClick={() => { void connections.refetch(); void providers.refetch() }}>{tx("Yeniden yükle")}</Button></p>
      : !connections.isPending && options.length === 0 && <p className="text-xs text-text-muted">{tx("Eşleşen bağlantı yok.")}</p>}
    {connections.data && <ListPager state={state} total={connections.data.totalCount} disabled={connections.isFetching} label={tx("Depolama bağlantıları sayfaları")} />}
    <p className="text-xs text-text-muted">{tx("Yalnızca seçilen bağlantılara ait kopyalar kotaya dahildir. Devre dışı hedefler de mevcut kopyalar için seçilebilir. Arama ve yenileme mevcut seçimleri değiştirmez.")}</p>
  </div>
}
