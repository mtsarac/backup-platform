import { tx, useLanguage } from '@/lib/i18n'
import { capabilityLabels } from './format'
import type { AgentCapability } from './types'
import { StatusPill } from '@/components/data/status-pill'

const supportOrder: Record<string, number> = { Supported: 0, Unknown: 1, Unsupported: 2 }
const groups = [
  { support: 'Supported', get title() { return tx("Desteklenenler") } },
  { support: 'Unknown', get title() { return tx("Durumu bilinmeyenler") } },
  { support: 'Unsupported', get title() { return tx("Desteklenmeyenler") } },
] as const

function sortCapabilities(capabilities: AgentCapability[]) {
  return [...capabilities].sort((a, b) => (supportOrder[a.support] ?? 1) - (supportOrder[b.support] ?? 1) || a.identifier.localeCompare(b.identifier))
}

/** Table summary: a count only; the full list lives on the Agent detail page. Never claims support for something not reported. */
export function CapabilitySummary({ capabilities }: { capabilities: AgentCapability[] }) {
  useLanguage()
  if (capabilities.length === 0) return <span className="text-text-muted">{tx("Henüz bildirilmedi")}</span>
  const supported = capabilities.filter((item) => item.support === 'Supported').length
  return <div><div>{capabilities.length}{' '}{tx("yetenek")}</div><div className="text-sm text-text-muted">{supported}{' '}{tx("destekleniyor")}</div></div>
}

/** Full list grouped by support state; unknown support values land with the unknown ones. */
export function CapabilityList({ capabilities }: { capabilities: AgentCapability[] }) {
  useLanguage()
  const known = new Set<string>(groups.map((group) => group.support))
  return <div className="space-y-5 text-[0.9375rem] text-text">
    {groups.map((group) => {
      const items = sortCapabilities(capabilities.filter((item) => group.support === 'Unknown' ? item.support === 'Unknown' || !known.has(item.support) : item.support === group.support))
      if (items.length === 0) return null
      return <div key={group.support} className="space-y-2">
        <h3 className="text-sm font-semibold text-text-muted">{group.title} ({items.length})</h3>
        <ul className="divide-y divide-border rounded-lg border border-border">
          {items.map((capability) => <li key={capability.identifier} className="flex flex-wrap items-center gap-x-3 gap-y-1 px-4 py-2.5">
            <span className="min-w-0 break-words font-medium">{capabilityLabels[capability.identifier] ?? capability.identifier}</span>
            <StatusPill domain="capability" status={capability.support} />
            {capabilityLabels[capability.identifier] && <code className="break-all text-xs text-text-muted">{capability.identifier}</code>}
            {capability.version && <span className="text-xs text-text-muted">{capability.version}</span>}
          </li>)}
        </ul>
      </div>
    })}
  </div>
}
