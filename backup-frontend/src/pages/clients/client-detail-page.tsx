import { useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { QueryState } from '@/components/data/query-state'
import { Button } from '@/components/ui/button'
import { useTenant } from '@/features/tenancy/use-tenant'
import { useAgent, agentQueryKeys } from '@/features/agents/hooks'
import { revokeAgent } from '@/features/agents/api'
import { AgentState, InfoRow } from '@/features/agents/presentation'
import { agentDate } from '@/features/agents/format'
import { CapabilityList } from '@/features/agents/capabilities'
import { ConfirmDialog } from '@/features/agents/confirm-dialog'
import { errorMessage } from '@/lib/api/failure-messages'

export function ClientDetailPage() {
  const { tenantId, canAdmin } = useTenant()
  const { id = '' } = useParams()
  return <AgentDetailContent key={`${tenantId}:${id}`} tenantId={tenantId} id={id} canAdmin={canAdmin} />
}

function AgentDetailContent({ tenantId, id, canAdmin }: { tenantId: string; id: string; canAdmin: boolean }) {
  const query = useAgent(tenantId, id)
  const client = useQueryClient()
  const [confirmOpen, setConfirmOpen] = useState(false)
  const revoke = useMutation({ mutationFn: () => revokeAgent(tenantId, id), onSuccess: async () => {
    setConfirmOpen(false)
    await Promise.all([
      client.invalidateQueries({ queryKey: agentQueryKeys.all(tenantId) }),
      client.invalidateQueries({ queryKey: ['tenant', tenantId, 'dashboard'] }),
    ])
  } })
  const agent = query.data
  return <div className="space-y-4">
    <Link to={`/tenants/${tenantId}/agents`} className="text-sm font-medium text-primary hover:underline">← Agent'lara dön</Link>
    {!id ? <p role="alert" className="text-text-muted">Agent bulunamadı.</p> : <QueryState isPending={query.isPending} error={query.error}>
      {agent && <>
        <div className="flex flex-wrap items-center justify-between gap-3"><h1 className="text-2xl font-semibold text-text-heading">{agent.hostName ?? 'Adsız agent'}</h1>{canAdmin && agent.status !== 'Revoked' && <Button variant="destructive" onClick={() => { revoke.reset(); setConfirmOpen(true) }}>Agent kaydını iptal et</Button>}</div>
        <div className="grid gap-4 lg:grid-cols-2">
          <section className="rounded-lg border border-border bg-surface p-4"><h2 className="font-medium text-text-heading">Kimlik ve kayıt</h2><dl className="mt-3 text-sm"><InfoRow label="Makine adı">{agent.hostName}</InfoRow><InfoRow label="Platform">{agent.platform}</InfoRow><InfoRow label="İşletim sistemi">{agent.osDescription}</InfoRow><InfoRow label="Kayıt zamanı">{agentDate(agent.enrolledAt)}</InfoRow><InfoRow label="Yaşam döngüsü"><AgentState value={agent.status} /></InfoRow></dl><p className="mt-3 text-xs text-text-muted">İptal ve devre dışı durumları bağlantı kesilmesinden bağımsızdır.</p></section>
          <section className="rounded-lg border border-border bg-surface p-4"><h2 className="font-medium text-text-heading">Bağlantı ve çalışma</h2><dl className="mt-3 text-sm"><InfoRow label="Bağlantı"><AgentState value={agent.connectivity} /></InfoRow><InfoRow label="Çalışma durumu"><AgentState value={agent.operationalStatus} /></InfoRow><InfoRow label="Son görülme">{agentDate(agent.lastSeenAt)}</InfoRow><InfoRow label="Son heartbeat">{agentDate(agent.lastHeartbeatAt)}</InfoRow><InfoRow label="Son bağlantı">{agentDate(agent.lastConnectedAt)}</InfoRow></dl></section>
          <section className="rounded-lg border border-border bg-surface p-4"><h2 className="font-medium text-text-heading">Sürümler</h2><dl className="mt-3 text-sm"><InfoRow label="Agent yazılımı">{agent.agentVersion}</InfoRow><InfoRow label="Protokol">{agent.protocolVersion}</InfoRow><InfoRow label="Çalışma ortamı">{agent.runtimeVersion}</InfoRow><InfoRow label="Bildirilen yetenek revizyonu">{agent.capabilityRevision}</InfoRow><InfoRow label="Kaydedilen yetenek revizyonu">{agent.capabilitiesRevision}</InfoRow></dl></section>
          <section className="rounded-lg border border-border bg-surface p-4">
            <h2 className="font-medium text-text-heading">Bildirilen yetenekler</h2>
            {agent.capabilities.length ? (
              <>
                <CapabilityList capabilities={agent.capabilities} />
                <p className="mt-3 text-xs text-text-muted">Son bildirim: {agentDate(agent.capabilitiesReportedAt)}. Agent düzeyindeki destek, SQL Server örneğinin gerçek desteğini garanti etmez; her çalıştırmadan önce pre-check yapılır.</p>
              </>
            ) : <p className="mt-3 text-sm text-text-muted">Henüz bildirilmedi.</p>}
          </section>
        </div>
      </>}
    </QueryState>}
    <ConfirmDialog open={confirmOpen} onClose={() => setConfirmOpen(false)} title="Agent kaydı iptal edilsin mi?" description="Bu işlem geri alınamaz. Agent artık bu çalışma alanına bağlanamaz; yeniden kayıt için yeni bir kimlik gerekir." pending={revoke.isPending} error={revoke.error ? errorMessage(revoke.error) : undefined} onConfirm={() => { if (canAdmin) revoke.mutate() }} />
  </div>
}
