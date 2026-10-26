import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { QueryState } from '@/components/data/query-state'
import { Button } from '@/components/ui/button'
import { Field } from '@/components/ui/field'
import { Notice } from '@/components/ui/notice'
import type { Agent } from '@/features/agents/types'
import { DatabaseTable } from '@/features/discovery/database-table'
import { useDiscoveryInstance, useDiscoveryInstances } from '@/features/discovery/hooks'
import { selectableNames } from '@/features/discovery/job-prefill'
import { canCreateJob, engineLabels, supportsDiscovery } from '@/features/discovery/status'
import type { DiscoveryInstanceDetail } from '@/features/discovery/types'
import { selectClass } from '@/components/ui/form-styles'

export interface DiscoverySourceProps {
  agents: readonly Agent[]
  agentId: string
  selected: readonly string[]
  onApply: (instance: DiscoveryInstanceDetail, databases: string[]) => void
  onClear: () => void
}

/** Read-only browsing of the inventory; nothing is created here, the wizard's final button does that. Mounted only when opened so closed wizards make no discovery requests. */
export function DiscoverySourcePicker({ agents, agentId: wizardAgentId, selected, onApply, onClear }: DiscoverySourceProps) {
  useLanguage()
  const [open, setOpen] = useState(false)
  const [agentId, setAgentId] = useState(wizardAgentId)
  const [instanceId, setInstanceId] = useState('')
  const [picked, setPicked] = useState<string[]>([])
  const supported = agents.filter(agent => supportsDiscovery(agent.capabilities))
  const instances = useDiscoveryInstances({ agentId }, open && Boolean(agentId))
  const detail = useDiscoveryInstance(open ? instanceId : '')
  const usable = (instances.data?.items ?? []).filter(canCreateJob)
  const instance = detail.data
  const names = instance ? selectableNames(instance, picked) : []
  return <div className="space-y-3 md:col-span-2">
    <div className="flex flex-wrap items-center gap-2">
      <Button type="button" variant="outline" aria-expanded={open} onClick={() => setOpen(value => !value)}>{tx("Keşfedilen veritabanlarından seç")}</Button>
      {selected.length > 0 && <Button type="button" variant="ghost" onClick={onClear}>{tx("Seçimi temizle")}</Button>}
    </div>
    {selected.length > 0 && <p className="text-sm text-text">{tx("Seçili veritabanları (")}{selected.length}): {selected.join(', ')}{tx(". Her biri için ayrı bir Backup Job oluşturulur.")}</p>}
    {open && <div className="space-y-3 rounded-lg border border-border bg-surface-2 p-4">
      {supported.length === 0 ? <Notice tone="info">{tx("Keşfi destekleyen Agent yok. Agent sürümü keşfi desteklemiyor; veritabanını elle girin.")}</Notice> : <>
        <Field id="discovery-agent" label={tx("Keşif Agent'ı")}>{control => <select {...control} className={`${selectClass} w-full`} value={agentId} onChange={event => { setAgentId(event.target.value); setInstanceId(''); setPicked([]) }}>
          <option value="">{tx("Agent seçin")}</option>{supported.map(agent => <option key={agent.agentId} value={agent.agentId}>{agent.hostName ?? tx('Adsız Agent')}</option>)}</select>}</Field>
        {agentId && <QueryState isPending={instances.isPending} error={instances.error}>
          <Field id="discovery-instance" label={tx("Instance")} hint={tx("Yalnızca kimlik doğrulanmış ve adresi bilinen Instance'lar listelenir.")}>{control => <select {...control} className={`${selectClass} w-full`} value={instanceId} onChange={event => { setInstanceId(event.target.value); setPicked([]) }}>
            <option value="">{tx("Instance seçin")}</option>{usable.map(item => <option key={item.id} value={item.id}>{item.displayName} · {engineLabels[item.engine]} · {item.host}:{item.port}</option>)}</select>}</Field>
          {usable.length === 0 && <p className="mt-2 text-sm text-text-muted">{tx("Kullanılabilir sunucu yok. Veritabanı bulma sayfasından giriş bilgilerini kaydedin.")}</p>}
        </QueryState>}
        {instanceId && <QueryState isPending={detail.isPending} error={detail.error}>
          {instance && <><DatabaseTable instance={instance} selected={picked} onChange={setPicked} />
            <Button type="button" disabled={names.length === 0} onClick={() => { onApply(instance, names); setOpen(false) }}>{tx("Seçimi uygula")}{names.length > 0 ? ` (${names.length})` : ''}</Button></>}
        </QueryState>}
      </>}
    </div>}
  </div>
}
