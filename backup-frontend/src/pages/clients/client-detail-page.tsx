import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { useParams } from 'react-router-dom'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { DataList, type Column } from '@/components/data/data-list'
import { QueryState } from '@/components/data/query-state'
import { RelativeTime } from '@/components/data/relative-time'
import { StatusPill } from '@/components/data/status-pill'
import { Button } from '@/components/ui/button'
import { ErrorNotice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { Section } from '@/components/ui/section'
import { TechnicalDetails, type TechnicalItem } from '@/components/ui/technical-details'
import { useTenant } from '@/features/tenancy/use-tenant'
import { useAgent, agentQueryKeys } from '@/features/agents/hooks'
import { revokeAgent } from '@/features/agents/api'
import type { AgentDetail } from '@/features/agents/types'
import { CapabilityList } from '@/features/agents/capabilities'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { useBackupJobs } from '@/features/backup-jobs/hooks'
import type { BackupJob } from '@/features/backup-jobs/types'
import { notFound } from '@/lib/api/failure-messages'
import { formatDateTime } from '@/lib/format'

export function ClientDetailPage() {
  useLanguage()
  const { tenantId, canAdmin } = useTenant()
  const { id = '' } = useParams()
  return <AgentDetailContent key={`${tenantId}:${id}`} tenantId={tenantId} id={id} canAdmin={canAdmin} />
}

/** Every identifier, version and lifecycle value of the computer; values the computer never reported are left out. */
function technicalItems(agent: AgentDetail): TechnicalItem[] {
  const date = (value: string | null | undefined) => value ? formatDateTime(value) : null
  return ([
    [tx("Bilgisayar kimliği"), agent.agentId],
    [tx("Platform"), agent.platform],
    [tx("İşletim sistemi"), agent.osDescription],
    [tx("Yedekleme programı sürümü"), agent.agentVersion],
    [tx("Protokol sürümü"), agent.protocolVersion],
    [tx("Çalışma ortamı"), agent.runtimeVersion],
    [tx("Yaşam döngüsü"), agent.status],
    [tx("Çalışma durumu"), agent.operationalStatus],
    [tx("Kayıt zamanı"), date(agent.enrolledAt)],
    [tx("Son durum sinyali"), date(agent.lastHeartbeatAt)],
    [tx("Son bağlantı"), date(agent.lastConnectedAt)],
    [tx("Yeteneklerin bildirilme zamanı"), date(agent.capabilitiesReportedAt)],
    [tx("Bildirilen yetenek revizyonu"), agent.capabilityRevision],
    [tx("Kaydedilen yetenek revizyonu"), agent.capabilitiesRevision],
  ] as const).flatMap(([label, value]) => value ? [{ label, value }] : [])
}

function AgentDetailContent({ tenantId, id, canAdmin }: { tenantId: string; id: string; canAdmin: boolean }) {
  useLanguage()
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
  const crumbs = [{ label: tx("Yedekleme bilgisayarları"), to: `/tenants/${tenantId}/agents` }, { label: agent?.hostName ?? tx('Adsız bilgisayar') }]
  return <div className="space-y-6">
    {!id ? <><PageHeader title={tx("Yedekleme bilgisayarı bulunamadı")} crumbs={crumbs} /><ErrorNotice {...notFound(tx("Yedekleme bilgisayarı bulunamadı."))} /></> : <QueryState isPending={query.isPending} error={query.error}>
      {agent && <>
        <PageHeader crumbs={crumbs} title={agent.hostName ?? tx('Adsız bilgisayar')}
          description={<>{tx("Son görülme")}: {agent.lastSeenAt ? <RelativeTime value={agent.lastSeenAt} /> : tx('Henüz bağlanmadı')}</>}
          meta={<><StatusPill status={agent.connectivity} />{agent.status !== 'Active' && <StatusPill domain="agent" status={agent.status} />}</>}
          actions={canAdmin && agent.status !== 'Revoked' && <Button variant="destructive" onClick={() => { revoke.reset(); setConfirmOpen(true) }}>{tx("Bilgisayar kaydını iptal et")}</Button>} />
        <AgentJobs agentId={id} />
        <TechnicalDetails items={technicalItems(agent)}>
          <section className="space-y-3">
            <h3 className="text-sm font-semibold text-text-heading">{tx("Bildirilen yetenekler")}</h3>
            {agent.capabilities.length ? <>
              <CapabilityList capabilities={agent.capabilities} />
              <p className="text-sm text-text-muted">{tx("Bilgisayar düzeyindeki destek, veritabanı sunucusunun gerçek desteğini garanti etmez; her yedekten önce ön kontrol yapılır.")}</p>
            </> : <p className="text-text-muted">{tx("Yetenekler henüz bildirilmedi.")}</p>}
          </section>
        </TechnicalDetails>
      </>}
    </QueryState>}
    <ConfirmDialog open={confirmOpen} onClose={() => setConfirmOpen(false)} title={tx("Bilgisayar kaydı iptal edilsin mi?")}
      consequence={{
        what: tx("Bu yedekleme bilgisayarı artık bu çalışma alanına bağlanamaz."),
        who: tx("Bu bilgisayarda çalışan yedekleme işleri durur."),
        reversible: tx("Hayır. Yeniden kayıt için yeni bir kimlik gerekir."),
      }}
      confirmLabel={tx("İptali onayla")} pending={revoke.isPending} error={revoke.error} onConfirm={() => { if (canAdmin) revoke.mutate() }} />
  </div>
}

function AgentJobs({ agentId }: { agentId: string }) {
  useLanguage()
  const jobs = useBackupJobs()
  const rows = (jobs.data ?? []).filter((job) => job.agentId === agentId)
  const columns: Column<BackupJob>[] = [
    { key: 'name', header: tx('Yedekleme işi'), card: 'title', wrap: true, render: (job) => <span className="font-medium text-text-heading">{job.name}</span> },
    { key: 'enabled', card: 'status', header: tx('Durum'), render: (job) => <StatusPill status={job.enabled ? 'Active' : 'Disabled'} /> },
    { key: 'lastRun', header: tx('Son sonuç'), render: (job) => job.lastRun ? <div className="flex flex-wrap items-center gap-2"><StatusPill status={job.lastRun.status} /><span className="text-sm text-text-muted"><RelativeTime value={job.lastRun.createdAt} /></span></div> : tx('Henüz çalışmadı') },
    { key: 'next', header: tx('Sonraki yedek'), render: (job) => job.nextOccurrenceAt ? <RelativeTime value={job.nextOccurrenceAt} /> : tx('Zamanlanmadı') },
  ]
  return <Section title={tx("Bu bilgisayarın yedekleme işleri")} flush>
    <QueryState isPending={jobs.isPending} error={jobs.error}>
      <DataList rows={rows} columns={columns} keyExtractor={(job) => job.id} emptyMessage={tx("Bu bilgisayar için tanımlı yedekleme işi yok.")} className="rounded-none border-0 shadow-none" />
    </QueryState>
  </Section>
}
