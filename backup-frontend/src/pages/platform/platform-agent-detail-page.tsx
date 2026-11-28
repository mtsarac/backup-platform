import { tx, useLanguage } from '@/lib/i18n'
import { Link, useParams } from 'react-router-dom'
import { RelativeTime } from '@/components/data/relative-time'
import { StatusPill } from '@/components/data/status-pill'
import { linkClass } from '@/components/ui/form-styles'
import { PageHeader } from '@/components/ui/page-header'
import { AgentTechnicalDetails } from '@/features/agents/agent-technical-details'
import { platformCrumb } from '@/features/platform/crumbs'
import { usePlatformAgent } from '@/features/platform/hooks'
import { platformPaths } from '@/features/platform/paths'
import { QueryState } from '@/components/data/query-state'
import { platformOnlyMessage } from '@/lib/api/failure-messages'

/** Connection state and last seen lead; lifecycle, system, versions and capabilities are collapsed in the technical details. */
export function PlatformAgentDetailPage() {
  useLanguage()
  const { tenantId = '', agentId = '' } = useParams()
  const query = usePlatformAgent(tenantId, agentId)
  const agent = query.data
  return <div className="space-y-6">
    <PageHeader crumbs={[platformCrumb, { label: tx('Yedekleme bilgisayarları'), to: platformPaths.agents() }]} title={agent ? agent.hostName ?? tx('Adsız bilgisayar') : tx('Yedekleme bilgisayarı')}
      description={agent && <>{tx("Çalışma alanı:")}{' '}<Link className={linkClass} to={platformPaths.tenant(agent.tenantId)}>{agent.tenantName}</Link>{' · '}<span>{tx("Son görülme")}{': '}{agent.lastSeenAt ? <RelativeTime value={agent.lastSeenAt} /> : tx('Henüz bağlanmadı')}</span></>}
      meta={agent && <><StatusPill domain="agent" status={agent.connectivity} />{agent.status !== 'Active' && <StatusPill domain="agent" status={agent.status} />}</>} />
    <QueryState forbiddenMessage={platformOnlyMessage} isPending={query.isPending} error={query.error} notFound={{ backTo: platformPaths.agents(), backLabel: tx('Yedekleme bilgisayarları') }}>
      {agent && <AgentTechnicalDetails agent={agent} />}
    </QueryState>
  </div>
}
