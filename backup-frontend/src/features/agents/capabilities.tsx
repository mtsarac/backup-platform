import { Badge } from '@/components/ui/badge'
import { capabilityLabels, capabilitySupportLabels } from './format'
import type { AgentCapability } from './types'

const supportTones: Record<string, 'success' | 'neutral' | 'warning'> = { Supported: 'success', Unsupported: 'neutral', Unknown: 'warning' }
const supportOrder: Record<string, number> = { Supported: 0, Unknown: 1, Unsupported: 2 }

function sortCapabilities(capabilities: AgentCapability[]) {
  return [...capabilities].sort((a, b) => (supportOrder[a.support] ?? 1) - (supportOrder[b.support] ?? 1) || a.identifier.localeCompare(b.identifier))
}

export function SupportBadge({ support }: { support: string }) {
  return <Badge tone={supportTones[support] ?? 'warning'}>{capabilitySupportLabels[support] ?? support}</Badge>
}

/** Table summary: supported capabilities by name; never claims support for something not reported. */
export function CapabilitySummary({ capabilities }: { capabilities: AgentCapability[] }) {
  if (capabilities.length === 0) return <span className="text-text-muted">Henüz bildirilmedi</span>
  const supported = capabilities.filter((item) => item.support === 'Supported')
  const others = capabilities.length - supported.length
  return <div className="space-y-1">
    <div className="flex flex-wrap gap-1">{sortCapabilities(supported).map((item) => <Badge key={item.identifier} tone="success">{capabilityLabels[item.identifier] ?? item.identifier}</Badge>)}</div>
    {others > 0 && <div className="text-xs text-text-muted">{others} yetenek desteklenmiyor veya bilinmiyor</div>}
  </div>
}

export function CapabilityList({ capabilities }: { capabilities: AgentCapability[] }) {
  return <ul className="mt-3 space-y-2 text-sm text-text">
    {sortCapabilities(capabilities).map((capability) => <li key={capability.identifier} className="flex flex-wrap items-center gap-2 break-words">
      <span className="font-medium">{capabilityLabels[capability.identifier] ?? capability.identifier}</span>
      <SupportBadge support={capability.support} />
      {capabilityLabels[capability.identifier] && <code className="text-xs text-text-muted">{capability.identifier}</code>}
      {capability.version && <span className="text-xs text-text-muted">{capability.version}</span>}
    </li>)}
  </ul>
}
