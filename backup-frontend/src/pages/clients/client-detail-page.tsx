import { useState } from 'react'
import { useParams } from 'react-router-dom'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { DataTable, type Column } from '@/components/data/data-table'
import { QueryState } from '@/components/data/query-state'
import { StatusPill } from '@/components/data/status-pill'
import { Button } from '@/components/ui/button'
import { Notice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { Fact, Facts, Section } from '@/components/ui/section'
import { Tabs } from '@/components/ui/tabs'
import { useTenant } from '@/features/tenancy/use-tenant'
import { useAgent, agentQueryKeys } from '@/features/agents/hooks'
import { revokeAgent } from '@/features/agents/api'
import { AgentState } from '@/features/agents/presentation'
import { agentDate } from '@/features/agents/format'
import { CapabilityList } from '@/features/agents/capabilities'
import { ConfirmDialog } from '@/features/agents/confirm-dialog'
import { useBackupJobs } from '@/features/backup-jobs/hooks'
import type { BackupJob } from '@/features/backup-jobs/types'
import { errorMessage } from '@/lib/api/failure-messages'

type Tab = 'general' | 'capabilities' | 'jobs'
const tabs = [{ value: 'general', label: 'Genel' }, { value: 'capabilities', label: 'Yetenekler' }, { value: 'jobs', label: 'İşler' }] as const

export function ClientDetailPage() {
  const { tenantId, canAdmin } = useTenant()
  const { id = '' } = useParams()
  return <AgentDetailContent key={`${tenantId}:${id}`} tenantId={tenantId} id={id} canAdmin={canAdmin} />
}

function AgentDetailContent({ tenantId, id, canAdmin }: { tenantId: string; id: string; canAdmin: boolean }) {
  const query = useAgent(tenantId, id)
  const client = useQueryClient()
  const [confirmOpen, setConfirmOpen] = useState(false)
  const [tab, setTab] = useState<Tab>('general')
  const revoke = useMutation({ mutationFn: () => revokeAgent(tenantId, id), onSuccess: async () => {
    setConfirmOpen(false)
    await Promise.all([
      client.invalidateQueries({ queryKey: agentQueryKeys.all(tenantId) }),
      client.invalidateQueries({ queryKey: ['tenant', tenantId, 'dashboard'] }),
    ])
  } })
  const agent = query.data
  const crumbs = [{ label: "Agent'lar", to: `/tenants/${tenantId}/agents` }, { label: agent?.hostName ?? 'Agent' }]
  return <div className="space-y-6">
    {!id ? <><PageHeader title="Agent bulunamadı" crumbs={crumbs} /><Notice tone="error">Agent bulunamadı.</Notice></> : <QueryState isPending={query.isPending} error={query.error}>
      {agent && <>
        <PageHeader crumbs={crumbs} title={agent.hostName ?? 'Adsız agent'} description={`${agent.platform} · ${agent.osDescription ?? 'İşletim sistemi bildirilmedi'}`}
          meta={<><StatusPill status={agent.connectivity} />{agent.status !== 'Active' && <AgentState value={agent.status} />}</>}
          actions={canAdmin && agent.status !== 'Revoked' && <Button variant="destructive" onClick={() => { revoke.reset(); setConfirmOpen(true) }}>Agent kaydını iptal et</Button>} />
        <Tabs label="Agent bölümleri" idPrefix="agent" value={tab} onChange={setTab} items={tabs} />
        <div role="tabpanel" id={`agent-panel-${tab}`} aria-labelledby={`agent-tab-${tab}`} className="space-y-6">
          {tab === 'general' && <>
            <Section title="Bağlantı ve çalışma" description="İptal ve devre dışı durumları bağlantı kesilmesinden bağımsızdır.">
              <Facts columns={3}>
                <Fact label="Bağlantı"><AgentState value={agent.connectivity} /></Fact>
                <Fact label="Çalışma durumu"><AgentState value={agent.operationalStatus} /></Fact>
                <Fact label="Yaşam döngüsü"><AgentState value={agent.status} /></Fact>
                <Fact label="Son görülme">{agentDate(agent.lastSeenAt)}</Fact>
                <Fact label="Son heartbeat">{agentDate(agent.lastHeartbeatAt)}</Fact>
                <Fact label="Son bağlantı">{agentDate(agent.lastConnectedAt)}</Fact>
              </Facts>
            </Section>
            <Section title="Kimlik ve kayıt">
              <Facts>
                <Fact label="Makine adı">{agent.hostName ?? 'Henüz bildirilmedi'}</Fact>
                <Fact label="Platform">{agent.platform}</Fact>
                <Fact label="İşletim sistemi">{agent.osDescription ?? 'Henüz bildirilmedi'}</Fact>
                <Fact label="Kayıt zamanı">{agentDate(agent.enrolledAt)}</Fact>
              </Facts>
            </Section>
            <Section title="Sürümler">
              <Facts columns={3}>
                <Fact label="Agent yazılımı">{agent.agentVersion ?? 'Henüz bildirilmedi'}</Fact>
                <Fact label="Protokol">{agent.protocolVersion ?? 'Henüz bildirilmedi'}</Fact>
                <Fact label="Çalışma ortamı">{agent.runtimeVersion ?? 'Henüz bildirilmedi'}</Fact>
              </Facts>
            </Section>
          </>}
          {tab === 'capabilities' && <Section title="Bildirilen yetenekler" description={`Son bildirim: ${agentDate(agent.capabilitiesReportedAt)}`}>
            <div className="space-y-5">
              <Facts>
                <Fact label="Bildirilen yetenek revizyonu">{agent.capabilityRevision ?? 'Henüz bildirilmedi'}</Fact>
                <Fact label="Kaydedilen yetenek revizyonu">{agent.capabilitiesRevision ?? 'Henüz bildirilmedi'}</Fact>
              </Facts>
              {agent.capabilities.length ? <>
                <CapabilityList capabilities={agent.capabilities} />
                <p className="text-sm text-text-muted">Agent düzeyindeki destek, SQL Server Instance'ın gerçek desteğini garanti etmez; her çalıştırmadan önce pre-check yapılır.</p>
              </> : <p className="text-text-muted">Henüz bildirilmedi.</p>}
            </div>
          </Section>}
          {tab === 'jobs' && <AgentJobs agentId={id} />}
        </div>
      </>}
    </QueryState>}
    <ConfirmDialog open={confirmOpen} onClose={() => setConfirmOpen(false)} title="Agent kaydı iptal edilsin mi?" description="Bu işlem geri alınamaz. Agent artık bu çalışma alanına bağlanamaz; yeniden kayıt için yeni bir kimlik gerekir." pending={revoke.isPending} error={revoke.error ? errorMessage(revoke.error) : undefined} onConfirm={() => { if (canAdmin) revoke.mutate() }} />
  </div>
}

/** Mounted only on its tab, so the Backup Job list is requested only when someone looks at it. */
function AgentJobs({ agentId }: { agentId: string }) {
  const jobs = useBackupJobs()
  const rows = (jobs.data ?? []).filter((job) => job.agentId === agentId)
  const columns: Column<BackupJob>[] = [
    { key: 'name', header: 'Backup Job', wrap: true, render: (job) => <span className="font-medium text-text-heading">{job.name}</span> },
    { key: 'enabled', header: 'Durum', render: (job) => <StatusPill status={job.enabled ? 'Active' : 'Disabled'} /> },
    { key: 'lastRun', header: 'Son çalıştırma', render: (job) => job.lastRun ? <div className="flex flex-wrap items-center gap-2"><StatusPill status={job.lastRun.status} /><span className="text-sm text-text-muted">{agentDate(job.lastRun.createdAt)}</span></div> : 'Henüz çalışmadı' },
    { key: 'next', header: 'Sonraki çalışma', render: (job) => job.nextOccurrenceAt ? agentDate(job.nextOccurrenceAt) : 'Zamanlanmadı' },
  ]
  return <Section title="Bu Agent'ın Backup Job'ları" flush>
    <QueryState isPending={jobs.isPending} error={jobs.error}>
      <DataTable rows={rows} columns={columns} keyExtractor={(job) => job.id} emptyMessage="Bu Agent için tanımlı Backup Job yok." className="rounded-none border-0 shadow-none" />
    </QueryState>
  </Section>
}
