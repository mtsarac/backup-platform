import { Link, useParams } from 'react-router-dom'
import { agentDate } from '@/features/agents/format'
import { CapabilityList } from '@/features/agents/capabilities'
import { AgentState, InfoRow } from '@/features/agents/presentation'
import { usePlatformAgent } from '@/features/platform/hooks'
import { platformPaths } from '@/features/platform/paths'
import { PlatformQueryState } from '@/features/platform/platform-query-state'
import { linkClass } from '@/features/platform/styles'

export function PlatformAgentDetailPage() {
  const { tenantId = '', agentId = '' } = useParams()
  const query = usePlatformAgent(tenantId, agentId)
  const agent = query.data
  return <div className="space-y-4">
    <Link to={platformPaths.agents()} className="text-sm font-medium text-primary hover:underline">← Agent'lara dön</Link>
    <PlatformQueryState isPending={query.isPending} error={query.error}>
      {agent && <>
        <div><h1 className="text-2xl font-semibold text-text-heading">{agent.hostName ?? 'Adsız agent'}</h1><p className="mt-1 text-sm text-text-muted">Tenant: <Link className={linkClass} to={platformPaths.tenant(agent.tenantId)}>{agent.tenantName}</Link> · Salt okunur görünüm</p></div>
        <div className="grid gap-4 lg:grid-cols-2">
          <section className="rounded-lg border border-border bg-surface p-4"><h2 className="font-medium text-text-heading">Kimlik ve kayıt</h2><dl className="mt-3 text-sm"><InfoRow label="Makine adı">{agent.hostName}</InfoRow><InfoRow label="Platform">{agent.platform}</InfoRow><InfoRow label="İşletim sistemi">{agent.osDescription}</InfoRow><InfoRow label="Kayıt zamanı">{agentDate(agent.enrolledAt)}</InfoRow><InfoRow label="Yaşam döngüsü"><AgentState value={agent.status} /></InfoRow></dl><p className="mt-3 text-xs text-text-muted">İptal ve devre dışı durumları bağlantı kesilmesinden bağımsızdır.</p></section>
          <section className="rounded-lg border border-border bg-surface p-4"><h2 className="font-medium text-text-heading">Bağlantı ve çalışma</h2><dl className="mt-3 text-sm"><InfoRow label="Bağlantı"><AgentState value={agent.connectivity} /></InfoRow><InfoRow label="Çalışma durumu"><AgentState value={agent.operationalStatus} /></InfoRow><InfoRow label="Son görülme">{agentDate(agent.lastSeenAt)}</InfoRow><InfoRow label="Son heartbeat">{agentDate(agent.lastHeartbeatAt)}</InfoRow><InfoRow label="Son bağlantı">{agentDate(agent.lastConnectedAt)}</InfoRow></dl></section>
          <section className="rounded-lg border border-border bg-surface p-4"><h2 className="font-medium text-text-heading">Sürümler</h2><dl className="mt-3 text-sm"><InfoRow label="Agent yazılımı">{agent.agentVersion}</InfoRow><InfoRow label="Protokol">{agent.protocolVersion}</InfoRow><InfoRow label="Çalışma ortamı">{agent.runtimeVersion}</InfoRow><InfoRow label="Bildirilen yetenek revizyonu">{agent.capabilityRevision}</InfoRow></dl></section>
          <section className="rounded-lg border border-border bg-surface p-4">
            <h2 className="font-medium text-text-heading">Bildirilen yetenekler</h2>
            {agent.capabilitiesReportedAt === null
              ? <p className="mt-3 text-sm text-text-muted">Henüz bildirilmedi.</p>
              : agent.capabilities.length === 0
                ? <p className="mt-3 text-sm text-text-muted">Agent yetenek bildirmedi. Son bildirim: {agentDate(agent.capabilitiesReportedAt)}.</p>
                : <><CapabilityList capabilities={agent.capabilities} /><p className="mt-3 text-xs text-text-muted">Son bildirim: {agentDate(agent.capabilitiesReportedAt)}. Agent düzeyindeki destek, SQL Server örneğinin gerçek desteğini garanti etmez.</p></>}
          </section>
        </div>
      </>}
    </PlatformQueryState>
  </div>
}
