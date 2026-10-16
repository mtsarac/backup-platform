import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { inputClass } from '@/features/platform/styles'
import { errorMessage } from '@/lib/api/failure-messages'
import { usePlatformConnections, usePlatformProviders } from './hooks'
import { connectionOptions } from './job-connection-picker-utils'

/** Quotas account for existing replicas, including disabled connections. Catalog refreshes never remove selected IDs. */
export function QuotaConnectionPicker({ values, onChange, invalid }: { values: readonly string[]; onChange: (values: string[]) => void; invalid?: boolean }) {
  useLanguage()
  const [draft, setDraft] = useState('')
  const [search, setSearch] = useState('')
  const [page, setPage] = useState(1)
  const [selectedLabels, setSelectedLabels] = useState<Record<string, string>>({})
  const connections = usePlatformConnections({ search, page, pageSize: 20 })
  const providers = usePlatformProviders()
  const options = connectionOptions(connections.data?.items ?? [], providers.data?.items ?? [], true)
  const error = connections.error || providers.error

  function searchConnections() { setSearch(draft.trim()); setPage(1) }
  function toggle(id: string, label: string) {
    setSelectedLabels((current) => ({ ...current, [id]: label }))
    onChange(values.includes(id) ? values.filter((value) => value !== id) : [...values, id])
  }

  return <div className="space-y-2">
    <div className="flex items-end gap-2">
      <label className="min-w-0 flex-1 text-xs text-text-muted">{tx("Tenant veya bağlantı adı ara")}<input className={inputClass} value={draft} onChange={(event) => setDraft(event.target.value)} onKeyDown={(event) => {
          if (event.key === 'Enter') { event.preventDefault(); searchConnections() }
        }} />
      </label>
      <Button type="button" variant="outline" onClick={searchConnections}>{tx("Ara")}</Button>
    </div>
    <fieldset className="space-y-2" aria-invalid={invalid}>
      <legend className="text-xs text-text-muted">{tx("Depolama bağlantıları listesi")}</legend>
      {values.map((id, index) => {
        const option = options.find((item) => item.id === id)
        return <label key={id} className="flex items-start gap-2 text-xs text-text">
          <input type="checkbox" checked onChange={() => onChange(values.filter((value) => value !== id))} />
          {option?.label ?? selectedLabels[id] ?? tx("Mevcut bağlantı {0} · Bu listede bulunamadı; seçim korunuyor", { "0": index + 1 })}
        </label>
      })}
      {options.filter((option) => !values.includes(option.id)).map((option) => <label key={option.id} className="flex items-start gap-2 text-xs text-text">
        <input type="checkbox" checked={false} disabled={connections.isFetching || providers.isPending || Boolean(error)} onChange={() => toggle(option.id, option.label)} />
        {tx(option.label)}
      </label>)}
    </fieldset>
    {connections.isFetching && <p role="status" className="text-xs text-text-muted">{tx("Bağlantılar yükleniyor…")}</p>}
    {error ? <p role="status" className="text-xs text-error">{errorMessage(error)} <button type="button" className="underline" onClick={() => { void connections.refetch(); void providers.refetch() }}>{tx("Yeniden yükle")}</button></p>
      : !connections.isPending && options.length === 0 && <p className="text-xs text-text-muted">{tx("Eşleşen bağlantı yok.")}</p>}
    <div className="flex items-center gap-2">
      <Button type="button" variant="outline" size="sm" disabled={page === 1 || connections.isFetching} onClick={() => setPage(page - 1)}>{tx("Önceki")}</Button>
      <span className="text-xs text-text-muted">{tx("Sayfa")}{' '}{page}</span>
      <Button type="button" variant="outline" size="sm" disabled={connections.isFetching || !connections.data || page * 20 >= connections.data.totalCount} onClick={() => setPage(page + 1)}>{tx("Sonraki")}</Button>
    </div>
    <p className="text-xs text-text-muted">{tx("Yalnızca seçilen bağlantılara ait kopyalar kotaya dahildir. Devre dışı hedefler de mevcut kopyalar için seçilebilir. Arama ve yenileme mevcut seçimleri değiştirmez.")}</p>
  </div>
}
