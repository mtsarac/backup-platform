import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { useParams } from 'react-router-dom'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { DataTable, type Column } from '@/components/data/data-table'
import { QueryState } from '@/components/data/query-state'
import { StatusPill } from '@/components/data/status-pill'
import { Button } from '@/components/ui/button'
import { ErrorNotice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { Fact, Facts, Section } from '@/components/ui/section'
import { Tabs } from '@/components/ui/tabs'
import { useTenant } from '@/features/tenancy/use-tenant'
import { useAgent, agentQueryKeys } from '@/features/agents/hooks'
import { revokeAgent } from '@/features/agents/api'
import { CapabilityList } from '@/features/agents/capabilities'
import { ConfirmDialog } from '@/features/agents/confirm-dialog'
import { useBackupJobs } from '@/features/backup-jobs/hooks'
import type { BackupJob } from '@/features/backup-jobs/types'
import { errorMessage, notFound } from '@/lib/api/failure-messages'
import { formatDateTime } from '@/lib/format'

type Tab = 'general' | 'capabilities' | 'jobs'
const tabs = [{ value: 'general', get label() { return tx("Genel") } }, { value: 'capabilities', get label() { return tx("Yetenekler") } }, { value: 'jobs', get label() { return tx("İşler") } }] as const

export function ClientDetailPage() {
  useLanguage()
  const { tenantId, canAdmin } = useTenant()
  const { id = '' } = useParams()
  return <AgentDetailContent key={`${tenantId}:${id}`} tenantId={tenantId} id={id} canAdmin={canAdmin} />
}

function AgentDetailContent({ tenantId, id, canAdmin }: { tenantId: string; id: string; canAdmin: boolean }) {
  useLanguage()
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
  const crumbs = [{ label: tx("Agent'lar"), to: `/tenants/${tenantId}/agents` }, { label: agent?.hostName ?? 'Agent' }]
  return <div className="space-y-6">
    {!id ? <><PageHeader title={tx("Agent bulunamadı")} crumbs={crumbs} /><ErrorNotice {...notFound(tx("Agent bulunamadı."))} /></> : <QueryState isPending={query.isPending} error={query.error}>
      {agent && <>
        <PageHeader crumbs={crumbs} title={agent.hostName ?? tx('Adsız agent')} description={`${agent.platform} · ${agent.osDescription ?? tx('İşletim sistemi bildirilmedi')}`}
          meta={<><StatusPill status={agent.connectivity} />{agent.status !== 'Active' && <StatusPill domain="agent" status={agent.status} />}</>}
          actions={canAdmin && agent.status !== 'Revoked' && <Button variant="destructive" onClick={() => { revoke.reset(); setConfirmOpen(true) }}>{tx("Agent kaydını iptal et")}</Button>} />
        <Tabs label={tx("Agent bölümleri")} idPrefix="agent" value={tab} onChange={setTab} items={tabs} />
        <div role="tabpanel" id={`agent-panel-${tab}`} aria-labelledby={`agent-tab-${tab}`} className="space-y-6">
          {tab === 'general' && <>
            <Section title={tx("Bağlantı ve çalışma")} description={tx("İptal ve devre dışı durumları bağlantı kesilmesinden bağımsızdır.")}>
              <Facts columns={3}>
                <Fact label={tx("Bağlantı")}><StatusPill domain="agent" status={agent.connectivity} /></Fact>
                <Fact label={tx("Çalışma durumu")}><StatusPill domain="agent" status={agent.operationalStatus} /></Fact>
                <Fact label={tx("Yaşam döngüsü")}><StatusPill domain="agent" status={agent.status} /></Fact>
                <Fact label={tx("Son görülme")}>{formatDateTime(agent.lastSeenAt, tx('Henüz bildirilmedi'))}</Fact>
                <Fact label={tx("Son heartbeat")}>{formatDateTime(agent.lastHeartbeatAt, tx('Henüz bildirilmedi'))}</Fact>
                <Fact label={tx("Son bağlantı")}>{formatDateTime(agent.lastConnectedAt, tx('Henüz bildirilmedi'))}</Fact>
              </Facts>
            </Section>
            <Section title={tx("Kimlik ve kayıt")}>
              <Facts>
                <Fact label={tx("Makine adı")}>{agent.hostName ?? tx('Henüz bildirilmedi')}</Fact>
                <Fact label="Platform">{agent.platform}</Fact>
                <Fact label={tx("İşletim sistemi")}>{agent.osDescription ?? tx('Henüz bildirilmedi')}</Fact>
                <Fact label={tx("Kayıt zamanı")}>{formatDateTime(agent.enrolledAt, tx('Henüz bildirilmedi'))}</Fact>
              </Facts>
            </Section>
            <Section title={tx("Sürümler")}>
              <Facts columns={3}>
                <Fact label={tx("Agent yazılımı")}>{agent.agentVersion ?? tx('Henüz bildirilmedi')}</Fact>
                <Fact label={tx("Protokol")}>{agent.protocolVersion ?? tx('Henüz bildirilmedi')}</Fact>
                <Fact label={tx("Çalışma ortamı")}>{agent.runtimeVersion ?? tx('Henüz bildirilmedi')}</Fact>
              </Facts>
            </Section>
          </>}
          {tab === 'capabilities' && <Section title={tx("Bildirilen yetenekler")} description={tx("Son bildirim: {0}", { "0": formatDateTime(agent.capabilitiesReportedAt, tx('Henüz bildirilmedi')) })}>
            <div className="space-y-5">
              <Facts>
                <Fact label={tx("Bildirilen yetenek revizyonu")}>{agent.capabilityRevision ?? tx('Henüz bildirilmedi')}</Fact>
                <Fact label={tx("Kaydedilen yetenek revizyonu")}>{agent.capabilitiesRevision ?? tx('Henüz bildirilmedi')}</Fact>
              </Facts>
              {agent.capabilities.length ? <>
                <CapabilityList capabilities={agent.capabilities} />
                <p className="text-sm text-text-muted">{tx("Agent düzeyindeki destek, veritabanı sunucusunun gerçek desteğini garanti etmez; her çalıştırmadan önce pre-check yapılır.")}</p>
              </> : <p className="text-text-muted">{tx("Henüz bildirilmedi.")}</p>}
            </div>
          </Section>}
          {tab === 'jobs' && <AgentJobs agentId={id} />}
        </div>
      </>}
    </QueryState>}
    <ConfirmDialog open={confirmOpen} onClose={() => setConfirmOpen(false)} title={tx("Agent kaydı iptal edilsin mi?")} description={tx("Bu işlem geri alınamaz. Agent artık bu çalışma alanına bağlanamaz; yeniden kayıt için yeni bir kimlik gerekir.")} pending={revoke.isPending} error={revoke.error ? errorMessage(revoke.error) : undefined} onConfirm={() => { if (canAdmin) revoke.mutate() }} />
  </div>
}

/** Mounted only on its tab, so the Backup Job list is requested only when someone looks at it. */
function AgentJobs({ agentId }: { agentId: string }) {
  useLanguage()
  const jobs = useBackupJobs()
  const rows = (jobs.data ?? []).filter((job) => job.agentId === agentId)
  const columns: Column<BackupJob>[] = [
    { key: 'name', header: tx('Backup Job'), wrap: true, render: (job) => <span className="font-medium text-text-heading">{job.name}</span> },
    { key: 'enabled', header: tx('Durum'), render: (job) => <StatusPill status={job.enabled ? 'Active' : 'Disabled'} /> },
    { key: 'lastRun', header: tx('Son çalıştırma'), render: (job) => job.lastRun ? <div className="flex flex-wrap items-center gap-2"><StatusPill status={job.lastRun.status} /><span className="text-sm text-text-muted">{formatDateTime(job.lastRun.createdAt, tx('Henüz bildirilmedi'))}</span></div> : tx('Henüz çalışmadı') },
    { key: 'next', header: tx('Sonraki çalışma'), render: (job) => job.nextOccurrenceAt ? formatDateTime(job.nextOccurrenceAt, tx('Henüz bildirilmedi')) : tx('Zamanlanmadı') },
  ]
  return <Section title={tx("Bu Agent'ın Backup Job'ları")} flush>
    <QueryState isPending={jobs.isPending} error={jobs.error}>
      <DataTable rows={rows} columns={columns} keyExtractor={(job) => job.id} emptyMessage={tx("Bu Agent için tanımlı Backup Job yok.")} className="rounded-none border-0 shadow-none" />
    </QueryState>
  </Section>
}
