import { tx, useLanguage } from '@/lib/i18n'
import { Link, useParams } from 'react-router-dom'
import { PageHeader } from '@/components/ui/page-header'
import { Fact, Facts, Section } from '@/components/ui/section'
import { Tabs } from '@/components/ui/tabs'
import { CapabilityList } from '@/features/agents/capabilities'
import { platformCrumb } from '@/features/platform/crumbs'
import { usePlatformAgent } from '@/features/platform/hooks'
import { platformPaths } from '@/features/platform/paths'
import { PlatformQueryState } from '@/features/platform/platform-query-state'
import { linkClass } from '@/components/ui/form-styles'
import { useListParams } from '@/features/platform/use-list-params'
import { formatDateTime } from '@/lib/format'
import { StatusPill } from '@/components/data/status-pill'

export function PlatformAgentDetailPage() {
  useLanguage()
  const { tenantId = '', agentId = '' } = useParams()
  const query = usePlatformAgent(tenantId, agentId)
  const agent = query.data
  const { get, update } = useListParams()
  const tab = get('tab') === 'capabilities' ? 'capabilities' : 'overview'
  return <div className="space-y-6">
    <PageHeader crumbs={[platformCrumb, { label: tx('Agents'), to: platformPaths.agents() }]} title={agent?.hostName ?? (query.isPending ? 'Agent' : tx('Adsız agent'))}
      description={agent && <>{tx("Tenant:")}{' '}<Link className={linkClass} to={platformPaths.tenant(agent.tenantId)}>{agent.tenantName}</Link>{' '}{tx("· Salt okunur görünüm")}</>}
      meta={agent && <><StatusPill domain="agent" status={agent.status} /><StatusPill domain="agent" status={agent.connectivity} /></>} />
    <PlatformQueryState isPending={query.isPending} error={query.error}>
      {agent && <>
        <Tabs idPrefix="platform-agent" label={tx("Agent bölümleri")} value={tab} onChange={(value) => update({ tab: value === 'overview' ? '' : value })} items={[{ value: 'overview', label: tx('Genel') }, { value: 'capabilities', label: tx("Yetenekler ({0})", { "0": agent.capabilityCount }) }]} />
        <div role="tabpanel" id={`platform-agent-panel-${tab}`} aria-labelledby={`platform-agent-tab-${tab}`} className="space-y-6">
          {tab === 'overview' ? <>
            <Section title={tx("Bağlantı ve çalışma")}><Facts columns={3}>
              <Fact label={tx("Bağlantı")}><StatusPill domain="agent" status={agent.connectivity} /></Fact><Fact label={tx("Çalışma durumu")}><StatusPill domain="agent" status={agent.operationalStatus} /></Fact>
              <Fact label={tx("Son görülme")}>{formatDateTime(agent.lastSeenAt, tx('Henüz bildirilmedi'))}</Fact><Fact label={tx("Son heartbeat")}>{formatDateTime(agent.lastHeartbeatAt, tx('Henüz bildirilmedi'))}</Fact><Fact label={tx("Son bağlantı")}>{formatDateTime(agent.lastConnectedAt, tx('Henüz bildirilmedi'))}</Fact>
            </Facts></Section>
            <Section title={tx("Kimlik ve kayıt")} description={tx("İptal ve devre dışı durumları bağlantı kesilmesinden bağımsızdır.")}><Facts columns={3}>
              <Fact label={tx("Makine adı")}>{agent.hostName ?? '—'}</Fact><Fact label="Platform">{agent.platform}</Fact><Fact label={tx("İşletim sistemi")}>{agent.osDescription ?? '—'}</Fact>
              <Fact label={tx("Kayıt zamanı")}>{formatDateTime(agent.enrolledAt, tx('Henüz bildirilmedi'))}</Fact><Fact label={tx("Yaşam döngüsü")}><StatusPill domain="agent" status={agent.status} /></Fact>
            </Facts></Section>
            <Section title={tx("Sürümler")}><Facts columns={3}>
              <Fact label={tx("Agent yazılımı")}>{agent.agentVersion ?? '—'}</Fact><Fact label={tx("Protokol")}>{agent.protocolVersion ?? '—'}</Fact><Fact label={tx("Çalışma ortamı")}>{agent.runtimeVersion ?? '—'}</Fact><Fact label={tx("Bildirilen yetenek revizyonu")}>{agent.capabilityRevision ?? '—'}</Fact>
            </Facts></Section>
          </> : <Section title={tx("Bildirilen yetenekler")} description={tx("Agent düzeyindeki destek, veritabanı sunucusunun gerçek desteğini garanti etmez.")}>
            {agent.capabilitiesReportedAt === null ? <p className="text-text-muted">{tx("Henüz bildirilmedi.")}</p>
              : agent.capabilities.length === 0 ? <p className="text-text-muted">{tx("Agent yetenek bildirmedi. Son bildirim:")}{' '}{formatDateTime(agent.capabilitiesReportedAt, tx('Henüz bildirilmedi'))}.</p>
                : <><CapabilityList capabilities={agent.capabilities} /><p className="mt-4 text-sm text-text-muted">{tx("Son bildirim:")}{' '}{formatDateTime(agent.capabilitiesReportedAt, tx('Henüz bildirilmedi'))}.</p></>}
          </Section>}
        </div>
      </>}
    </PlatformQueryState>
  </div>
}
