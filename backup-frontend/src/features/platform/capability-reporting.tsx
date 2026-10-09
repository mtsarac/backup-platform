import { agentDate } from '@/features/agents/format'
import type { PlatformAgent } from './types'

/** `capabilitiesReportedAt === null` means the agent never reported; never imply support from a count of zero. */
export function CapabilityReporting({ agent }: { agent: Pick<PlatformAgent, 'capabilitiesReportedAt' | 'capabilityCount'> }) {
  if (agent.capabilitiesReportedAt === null) return <span className="text-text-muted">Henüz bildirilmedi</span>
  return <div><div>{agent.capabilityCount} yetenek</div><div className="text-xs text-text-muted">Son bildirim: {agentDate(agent.capabilitiesReportedAt)}</div></div>
}
