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
import { discoveryKeys, useDiscoveryInstances, useRescanAgent } from '@/features/discovery/hooks'
import { canCreateJob, databaseSelectable, engineLabels, supportsDiscovery } from '@/features/discovery/status'
import { useDiscoveryAgents } from '@/features/discovery/hooks'
import { useTenant } from '@/features/tenancy/use-tenant'
import { autoJobName, itemKey, type FirstBackupItem } from './first-backup-model'

/** "Ne yedeklensin": scans the chosen computer on arrival and lists what it found, all ticked; jobs are named automatically and only renamed on request. */
export function DataStep({ agents, names, onNames, picked, onPicked, onNext }: {
  agents: readonly Agent[]; names: Record<string, string>; onNames: (names: Record<string, string>) => void
  /** `null` means untouched: every database found is ticked. */
  picked: ReadonlySet<string> | null; onPicked: (picked: ReadonlySet<string>) => void; onNext: (items: FirstBackupItem[]) => void
}) {
  useLanguage()
  const { tenantId } = useTenant()
  const capable = agents.filter(agent => supportsDiscovery(agent.capabilities))
  const [chosen, setChosen] = useState(capable.find(agent => agent.connectivity === 'online')?.agentId ?? capable[0]?.agentId ?? '')
  const agentId = capable.some(agent => agent.agentId === chosen) ? chosen : ''
  const rescan = useRescanAgent()
  const scanned = useRef('')
  useEffect(() => {
    if (!agentId || scanned.current === agentId) return
    scanned.current = agentId
    rescan.mutate(agentId)
  }, [agentId]) // oxlint-disable-line react-hooks/exhaustive-deps -- one automatic scan per computer, not per render
  const { scanning: polling } = useDiscoveryAgents(Boolean(agentId))
  const scanning = Boolean(agentId) && (rescan.isIdle || rescan.isPending || polling)
  const instances = useDiscoveryInstances({ agentId }, Boolean(agentId))
  const usable = (instances.data?.items ?? []).filter(canCreateJob)
  const details = useQueries({ queries: usable.map(item => ({ queryKey: discoveryKeys.instance(tenantId, item.id), queryFn: ({ signal }: { signal: AbortSignal }) => getDiscoveryInstance(tenantId, item.id, signal) })) })
  const loaded = details.every(detail => !detail.isPending)
  const items = details.flatMap(detail => detail.data ? detail.data.databases.filter(database => databaseSelectable(database, detail.data)).map(database => ({ key: itemKey(detail.data.id, database.name), instance: detail.data, database: database.name })) : [])
  const isPicked = (key: string) => picked ? picked.has(key) : true
  const chosenItems = items.filter(item => isPicked(item.key))
  const [renaming, setRenaming] = useState<string | null>(null)
  const toggle = (key: string, checked: boolean) => {
    const next = new Set(picked ?? items.map(item => item.key))
    if (checked) next.add(key); else next.delete(key)
    onPicked(next)
  }
  if (capable.length === 0) return <Notice tone="info" action={<Link className={buttonVariants({ variant: 'outline' })} to={`/tenants/${tenantId}/backup-jobs/new`}>{tx('Elle yapılandır')}</Link>}>{tx('Otomatik taramayı destekleyen bilgisayar yok.')}</Notice>
  return <section aria-labelledby="first-backup-data" className="space-y-4">
    <h2 id="first-backup-data" className="text-lg font-semibold text-text-heading">{tx('Ne yedeklensin?')}</h2>
    {capable.length > 1 && <Field label={tx('Yedekleme bilgisayarı')}>{control => <Select {...control} value={agentId} onChange={event => { setChosen(event.target.value); onPicked(new Set()) }}>
      {capable.map(agent => <option key={agent.agentId} value={agent.agentId}>{agent.hostName ?? tx('Adsız bilgisayar')}</option>)}</Select>}</Field>}
    {rescan.error && <ErrorNotice error={rescan.error} onRetry={() => rescan.mutate(agentId)} />}
    {scanning && <p role="status" className="text-sm text-text-muted">{tx('Bilgisayar taranıyor…')}</p>}
    <QueryState isPending={instances.isPending} error={instances.error}>
      {!loaded || scanning ? null : items.length === 0
        ? <Notice tone="info" title={tx('Veri bulunamadı')} action={<Link className={buttonVariants({ variant: 'outline' })} to={`/tenants/${tenantId}/backup-jobs/new`}>{tx('Elle yapılandır')}</Link>}>{tx('Bu bilgisayarda yedeklenebilir veritabanı bulunamadı.')}</Notice>
        : <ul className="divide-y divide-border rounded-lg border border-border bg-surface">{items.map(item => {
          const name = names[item.key] ?? autoJobName(item.database)
          return <li key={item.key} className="space-y-1 px-4 py-3">
            <CheckField label={<span><span className="font-medium text-text-heading">{item.database}</span> <span className="text-text-muted">· {engineLabels[item.instance.engine]} · {item.instance.displayName}</span></span>} checked={isPicked(item.key)} onChange={event => toggle(item.key, event.target.checked)} />
            {isPicked(item.key) && (renaming === item.key
              ? <Field label={tx('İş adı')}>{control => <TextInput {...control} value={name} maxLength={200} onChange={event => onNames({ ...names, [item.key]: event.target.value })} onBlur={() => setRenaming(null)} autoFocus />}</Field>
              : <p className="pl-8 text-sm text-text-muted">{name} · <button type="button" className={linkClass} aria-label={tx('{0}: Adı değiştir', { '0': item.database })} onClick={() => setRenaming(item.key)}>{tx('Adı değiştir')}</button></p>)}
          </li>
        })}</ul>}
    </QueryState>
    <div className="flex justify-end"><Button type="button" disabled={chosenItems.length === 0 || chosenItems.some(item => !(names[item.key] ?? autoJobName(item.database)).trim())} onClick={() => onNext(chosenItems)}>{tx('İleri')}</Button></div>
  </section>
}
