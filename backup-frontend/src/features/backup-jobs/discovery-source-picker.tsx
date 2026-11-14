import { tx, useLanguage } from '@/lib/i18n'
import { Link } from 'react-router-dom'
import { Select } from '@/components/ui/text-input'
import { useState } from 'react'
import { QueryState } from '@/components/data/query-state'
import { Button } from '@/components/ui/button'
import { Field } from '@/components/ui/field'
import { linkClass } from '@/components/ui/form-styles'
import { Notice } from '@/components/ui/notice'
import type { Agent } from '@/features/agents/types'
import { DatabaseTable } from '@/features/discovery/database-table'
import { useDiscoveryInstance, useDiscoveryInstances } from '@/features/discovery/hooks'
import { selectableNames } from '@/features/discovery/job-prefill'
import { canCreateJob, engineLabels, supportsDiscovery } from '@/features/discovery/status'
import type { DiscoveryInstanceDetail } from '@/features/discovery/types'
import { discoveryPath } from '@/lib/routes'
import { useTenant } from '@/features/tenancy/use-tenant'

export interface DiscoverySourceProps {
  agents: readonly Agent[]
  /** Computer and instance to start on (a prefilled link whose databases are no longer selectable). */
  agentId: string
  instanceId?: string
  onApply: (instance: DiscoveryInstanceDetail, databases: string[]) => void
}

/** The one place a new job's computer, instance and databases are chosen from the inventory; nothing is created here, the wizard's final button does that. */
export function DiscoverySourcePicker({ agents, agentId: startAgent, instanceId: startInstance = '', onApply }: DiscoverySourceProps) {
  useLanguage()
  const { tenantId } = useTenant()
  const [pickedAgent, setPickedAgent] = useState(startAgent)
  const [pickedInstance, setPickedInstance] = useState(startInstance)
  const [picked, setPicked] = useState<string[]>([])
  const supported = agents.filter(agent => supportsDiscovery(agent.capabilities))
  // A single capable computer needs no question; a start value that cannot discover is ignored.
  const agentId = supported.some(agent => agent.agentId === pickedAgent) ? pickedAgent : supported.length === 1 ? supported[0].agentId : ''
  const instances = useDiscoveryInstances({ agentId }, Boolean(agentId))
  const usable = (instances.data?.items ?? []).filter(canCreateJob)
  const instanceId = usable.some(item => item.id === pickedInstance) ? pickedInstance : ''
  const detail = useDiscoveryInstance(instanceId)
  const instance = detail.data
  const names = instance ? selectableNames(instance, picked) : []
  if (supported.length === 0) return <Notice tone="info">{tx("Otomatik keşfi destekleyen bilgisayar yok. Kaynağı elle yapılandırın.")}</Notice>
  return <div className="space-y-3">
    {supported.length === 1 && <p className="text-sm text-text"><span className="font-medium text-text-heading">{tx("Yedekleme bilgisayarı")}:</span> {supported[0].hostName ?? tx('Adsız bilgisayar')}</p>}
    {supported.length > 1 && <Field id="discovery-agent" label={tx("Keşif Agent'ı")}>{control => <Select {...control} value={agentId} onChange={event => { setPickedAgent(event.target.value); setPickedInstance(''); setPicked([]) }}>
      <option value="">{tx("Agent seçin")}</option>{supported.map(agent => <option key={agent.agentId} value={agent.agentId}>{agent.hostName ?? tx('Adsız Agent')}</option>)}</Select>}</Field>}
    {agentId && <QueryState isPending={instances.isPending} error={instances.error}>
      {usable.length === 0 ? <Notice tone="info">{tx("Bu bilgisayarda kullanılabilir veritabanı sunucusu yok.")}{' '}<Link className={linkClass} to={discoveryPath(tenantId)}>{tx("Veritabanı bulma sayfasına gidin")}</Link></Notice>
        : <Field id="discovery-instance" label={tx("Instance")} hint={tx("Yalnızca kimlik doğrulanmış ve adresi bilinen Instance'lar listelenir.")}>{control => <Select {...control} value={instanceId} onChange={event => { setPickedInstance(event.target.value); setPicked([]) }}>
          <option value="">{tx("Instance seçin")}</option>{usable.map(item => <option key={item.id} value={item.id}>{item.displayName} · {engineLabels[item.engine]} · {item.host}:{item.port}</option>)}</Select>}</Field>}
    </QueryState>}
    {instanceId && <QueryState isPending={detail.isPending} error={detail.error}>
      {instance && <><DatabaseTable instance={instance} selected={picked} onChange={setPicked} />
        <Button type="button" disabled={names.length === 0} onClick={() => onApply(instance, names)}>{tx("Seçimi uygula")}{names.length > 0 ? ` (${names.length})` : ''}</Button></>}
    </QueryState>}
  </div>
}
