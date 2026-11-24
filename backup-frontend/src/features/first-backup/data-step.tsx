import { tx, useLanguage } from '@/lib/i18n'
import { useEffect, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import { useQueries } from '@tanstack/react-query'
import { buttonVariants } from '@/components/ui/button-variants'
import { Button } from '@/components/ui/button'
import { CheckField } from '@/components/ui/check-field'
import { Field } from '@/components/ui/field'
import { linkClass } from '@/components/ui/form-styles'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { Select, TextInput } from '@/components/ui/text-input'
import { QueryState } from '@/components/data/query-state'
import type { Agent } from '@/features/agents/types'
import { getDiscoveryInstance } from '@/features/discovery/api'
import { discoveryInstancePath } from '@/lib/routes'
import { discoveryKeys, useDiscoveryAgents, useDiscoveryInstances, useRescanAgent } from '@/features/discovery/hooks'
import { foldSearch } from '@/features/lists/fold-search'
import { canCreateJob, databaseSelectable, engineLabels, isRescanThrottled, isScanActive, supportsDiscovery } from '@/features/discovery/status'
import { useTenant } from '@/features/tenancy/use-tenant'
import { autoJobName, awaitingCapabilities, itemKey, type FirstBackupItem } from './first-backup-model'
import { actionName } from '@/lib/accessible-name'

/** "Ne yedeklensin": scans the chosen computer on arrival and lists what it found, nothing ticked; jobs are named automatically and only renamed on request. */
export function DataStep({ agents, names, onNames, picked, onPicked, onNext }: {
  agents: readonly Agent[]; names: Record<string, string>; onNames: (names: Record<string, string>) => void
  picked: ReadonlySet<string>; onPicked: (picked: ReadonlySet<string>) => void; onNext: (items: FirstBackupItem[]) => void
}) {
  useLanguage()
  const { tenantId } = useTenant()
  const capable = agents.filter(agent => supportsDiscovery(agent.capabilities))
  const [chosen, setChosen] = useState('')
  // Derived on every render: a computer whose capabilities arrive later must be adopted, not skipped.
  const computer = capable.find(agent => agent.agentId === chosen) ?? capable.find(agent => agent.connectivity === 'online') ?? capable[0]
  const agentId = computer?.agentId ?? ''
  const rescan = useRescanAgent()
  const scanned = useRef('')
  useEffect(() => {
    if (!agentId || scanned.current === agentId) return
    scanned.current = agentId
    rescan.mutate(agentId)
  }, [agentId]) // oxlint-disable-line react-hooks/exhaustive-deps -- one automatic scan per computer, not per render
  // Only this computer's own scan counts: another computer (or a revoked one) with a pending scan must not keep this list waiting.
  const statuses = useDiscoveryAgents(Boolean(agentId), agentId)
  const online = computer?.connectivity === 'online'
  const queued = isScanActive(statuses.data?.items.filter(status => status.agentId === agentId))
  // An offline computer only queues the scan: say so instead of waiting for it.
  const scanning = Boolean(agentId) && online && (rescan.isIdle || rescan.isPending || queued)
  const waitingOffline = Boolean(agentId) && !online && (rescan.isIdle || rescan.isPending || queued)
  const instances = useDiscoveryInstances({ agentId }, Boolean(agentId))
  const usable = (instances.data?.items ?? []).filter(canCreateJob)
  const needLogin = (instances.data?.items ?? []).filter(item => item.presence === 'Present' && item.access !== 'Authenticated')
  const details = useQueries({ queries: usable.map(item => ({ queryKey: discoveryKeys.instance(tenantId, item.id), queryFn: ({ signal }: { signal: AbortSignal }) => getDiscoveryInstance(tenantId, item.id, signal) })) })
  const loaded = details.every(detail => !detail.isPending)
  const items = details.flatMap(detail => detail.data ? detail.data.databases.filter(database => databaseSelectable(database, detail.data)).map(database => ({ key: itemKey(detail.data.id, database.name), instance: detail.data, database: database.name })) : [])
  const isPicked = (key: string) => picked.has(key)
  const chosenItems = items.filter(item => isPicked(item.key))
  const [renaming, setRenaming] = useState<string | null>(null)
  const [search, setSearch] = useState('')
  const needle = foldSearch(search.trim())
  const shown = needle ? items.filter(item => foldSearch(`${item.database} ${item.instance.displayName}`).includes(needle)) : items
  const toggle = (key: string, checked: boolean) => {
    const next = new Set(picked)
    if (checked) next.add(key); else next.delete(key)
    onPicked(next)
  }
  const manual = <Link className={buttonVariants({ variant: 'outline' })} to={`/tenants/${tenantId}/backup-jobs/new`}>{tx('Elle yapılandır')}</Link>
  if (capable.length === 0) return awaitingCapabilities(agents)
    ? <Notice tone="info" title={tx('Bilgisayar hazırlanıyor…')} action={manual}>{tx('Bilgisayar nelerin yedeklenebileceğini bildiriyor; birkaç saniye sürer.')}</Notice>
    : <Notice tone="info" action={manual}>{tx('Otomatik taramayı destekleyen bilgisayar yok.')}</Notice>
  return <section aria-labelledby="first-backup-data" className="space-y-4">
    <h2 id="first-backup-data" className="text-lg font-semibold text-text-heading">{tx('Ne yedeklensin?')}</h2>
    {capable.length > 1
      ? <Field label={tx('Yedekleme bilgisayarı')}>{control => <Select {...control} value={agentId} onChange={event => { setChosen(event.target.value); onPicked(new Set()); setSearch('') }}>
        {capable.map(agent => <option key={agent.agentId} value={agent.agentId}>{agent.hostName ?? tx('Adsız bilgisayar')}</option>)}</Select>}</Field>
      : <p className="text-sm text-text-muted">{tx('Yedekleme bilgisayarı:')} <span className="font-medium text-text-heading">{computer?.hostName ?? tx('Adsız bilgisayar')}</span></p>}
    {/* A throttled scan was just requested: nothing is wrong, the scan state below tells it. */}
    {rescan.error && !isRescanThrottled(rescan.error) && <ErrorNotice error={rescan.error} onRetry={() => rescan.mutate(agentId)} />}
    {scanning && <p role="status" className="text-sm text-text-muted">{tx('Bilgisayar taranıyor…')}</p>}
    {waitingOffline && <p role="status" className="text-sm text-text-muted">{tx('Bu bilgisayar şu anda çevrimdışı; tarama bilgisayar yeniden bağlandığında çalışır.')}</p>}
    <QueryState isPending={instances.isPending} error={instances.error}>
      {!loaded || scanning ? null : items.length === 0
        ? needLogin.length > 0
          ? <Notice tone="info" action={manual}>{tx('Veritabanı sunucusu bulundu ama listelemek için giriş bilgisi gerekiyor.')} {needLogin.map((item, index) => <span key={item.id}>{index > 0 && ', '}<Link className={linkClass} to={discoveryInstancePath(tenantId, item.id)}>{item.displayName}</Link></span>)}</Notice>
          : <Notice tone="info" title={tx('Veri bulunamadı')} action={manual}>{tx('Bu bilgisayarda yedeklenebilir veritabanı bulunamadı.')}</Notice>
        : <>
          {items.length > 1 && <TextInput type="search" aria-label={tx('Veritabanı ara')} placeholder={tx('Veritabanı veya sunucu adı')} value={search} onChange={event => setSearch(event.target.value)} />}
          {shown.length === 0 ? <p className="text-sm text-text-muted">{tx('Aramaya uyan veritabanı yok.')}</p> : <ul className="divide-y divide-border rounded-lg border border-border bg-surface">{shown.map(item => {
          const name = names[item.key] ?? autoJobName(item.database)
          return <li key={item.key} className="space-y-1 px-4 py-3">
            <CheckField label={<span><span className="font-medium text-text-heading">{item.database}</span> <span className="text-text-muted">· {engineLabels[item.instance.engine]} · {item.instance.displayName}</span></span>} checked={isPicked(item.key)} onChange={event => toggle(item.key, event.target.checked)} />
            {isPicked(item.key) && (renaming === item.key
              ? <Field label={tx('İş adı')}>{control => <TextInput {...control} value={name} maxLength={200} onChange={event => onNames({ ...names, [item.key]: event.target.value })} onBlur={() => setRenaming(null)} autoFocus />}</Field>
              : <p className="pl-8 text-sm text-text-muted">{name} · <button type="button" className={linkClass} aria-label={actionName(tx("Adı değiştir"), item.database)} onClick={() => setRenaming(item.key)}>{tx('Adı değiştir')}</button></p>)}
          </li>
        })}</ul>}
        </>}
    </QueryState>
    <div className="flex items-center justify-end gap-3">{chosenItems.length > 0 && <span className="text-sm text-text-muted">{tx('{0} seçili', { '0': chosenItems.length })}</span>}<Button type="button" disabled={chosenItems.length === 0 || chosenItems.some(item => !(names[item.key] ?? autoJobName(item.database)).trim())} onClick={() => onNext(chosenItems)}>{tx('İleri')}</Button></div>
  </section>
}
