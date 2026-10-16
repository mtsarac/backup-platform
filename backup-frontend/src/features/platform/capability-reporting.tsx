import { tx, useLanguage } from '@/lib/i18n'
import { agentDate } from '@/features/agents/format'
import type { PlatformAgent } from './types'

/** `capabilitiesReportedAt === null` means the agent never reported; never imply support from a count of zero. */
export function CapabilityReporting({ agent }: { agent: Pick<PlatformAgent, 'capabilitiesReportedAt' | 'capabilityCount'> }) {
  useLanguage()
  if (agent.capabilitiesReportedAt === null) return <span className="text-text-muted">{tx("Henüz bildirilmedi")}</span>
  return <div><div>{agent.capabilityCount}{' '}{tx("yetenek")}</div><div className="text-xs text-text-muted">{tx("Son bildirim:")}{' '}{agentDate(agent.capabilitiesReportedAt)}</div></div>
}
