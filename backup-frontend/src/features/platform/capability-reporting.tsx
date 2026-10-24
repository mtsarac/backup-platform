import { tx, useLanguage } from '@/lib/i18n'
import type { PlatformAgent } from './types'
import { formatDateTime } from '@/lib/format'

/** `capabilitiesReportedAt === null` means the agent never reported; never imply support from a count of zero. */
export function CapabilityReporting({ agent }: { agent: Pick<PlatformAgent, 'capabilitiesReportedAt' | 'capabilityCount'> }) {
  useLanguage()
  if (agent.capabilitiesReportedAt === null) return <span className="text-text-muted">{tx("Henüz bildirilmedi")}</span>
  return <div><div>{agent.capabilityCount}{' '}{tx("yetenek")}</div><div className="text-xs text-text-muted">{tx("Son bildirim:")}{' '}{formatDateTime(agent.capabilitiesReportedAt, tx('Henüz bildirilmedi'))}</div></div>
}
