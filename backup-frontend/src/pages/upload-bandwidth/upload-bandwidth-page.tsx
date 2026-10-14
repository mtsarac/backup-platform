import { useState } from 'react'
import { DataTable } from '@/components/data/data-table'
import type { Column } from '@/components/data/data-table'
import { QueryState } from '@/components/data/query-state'
import { Button } from '@/components/ui/button'
import { useAgents } from '@/features/agents/hooks'
import type { Agent } from '@/features/agents/types'
import { useBackupJobs } from '@/features/backup-jobs/hooks'
import type { BackupJob } from '@/features/backup-jobs/types'
import { useTenant } from '@/features/tenancy/use-tenant'
import { LimitDialog } from '@/features/upload-bandwidth/limit-dialog'
import { useUploadBandwidth } from '@/features/upload-bandwidth/hooks'
import type { BandwidthTarget } from '@/features/upload-bandwidth/types'
import { formatBandwidth, formatExactRate } from '@/features/upload-bandwidth/units'

interface Editing { target: BandwidthTarget; name: string; current: number | null }

function Limit({ value }: { value: number | null | undefined }) {
  if (value == null) return <span className="text-text-muted">Limitsiz</span>
  return <span>{formatBandwidth(value)} <span className="text-xs text-text-muted">({formatExactRate(value)})</span></span>
}

export function UploadBandwidthPage() {
  const { tenant, tenantId, canAdmin } = useTenant()
  const limits = useUploadBandwidth()
  const agents = useAgents(tenantId)
  const jobs = useBackupJobs()
  const [editing, setEditing] = useState<Editing | null>(null)
  const data = limits.data
  const agentLimits = new Map(data?.agents.map(item => [item.agentId, item.bytesPerSecond]))
  const jobLimits = new Map(data?.jobs.map(item => [item.jobId, item.bytesPerSecond]))
  const edit = (target: BandwidthTarget, name: string, current: number | null | undefined) => canAdmin
    ? <Button variant="outline" size="sm" aria-label={`${name} yükleme hız sınırını düzenle`} onClick={() => setEditing({ target, name, current: current ?? null })}>Düzenle</Button>
    : null
  const agentColumns: Column<Agent>[] = [
    { key: 'name', header: 'Agent', wrap: true, render: agent => agent.hostName || agent.agentId },
    { key: 'limit', header: 'Hız sınırı', wrap: true, render: agent => <Limit value={agentLimits.get(agent.agentId)} /> },
    ...(canAdmin ? [{ key: 'actions', header: 'İşlemler', render: (agent: Agent) => edit({ scope: 'agent', id: agent.agentId }, agent.hostName || agent.agentId, agentLimits.get(agent.agentId)) }] : []),
  ]
  const jobColumns: Column<BackupJob>[] = [
    { key: 'name', header: 'Backup Job', wrap: true, render: job => job.name },
    { key: 'agent', header: 'Agent', wrap: true, render: job => job.agentName || '—' },
    { key: 'limit', header: 'Hız sınırı', wrap: true, render: job => <Limit value={jobLimits.get(job.id)} /> },
    ...(canAdmin ? [{ key: 'actions', header: 'İşlemler', render: (job: BackupJob) => edit({ scope: 'job', id: job.id }, job.name, jobLimits.get(job.id)) }] : []),
  ]
  return <div className="space-y-6">
    <div>
      <h1 className="text-2xl font-semibold text-text-heading">Upload Bandwidth</h1>
      <p className="mt-1 text-sm text-text-muted">{tenant.name} · Yedekleme yüklemeleri için bayt/saniye cinsinden hız sınırları. Sınırlar birbirinden bağımsız üst sınırlardır ve birlikte uygulanır; en kısıtlayıcı olan geçerli olur. Değişiklikler çalışan yüklemelere birkaç saniye içinde yansır. Geri yükleme/indirme, checksum ve heartbeat sınırlanmaz.</p>
      {!canAdmin && <p className="mt-1 text-sm text-text-muted">Hız sınırlarını yalnızca Owner / Admin değiştirebilir.</p>}
    </div>
    <QueryState isPending={limits.isPending} error={limits.error}>
      <section aria-labelledby="bandwidth-tenant-heading" className="space-y-3 rounded-lg border border-border bg-surface p-4">
        <h2 id="bandwidth-tenant-heading" className="font-medium text-text-heading">Tenant sınırı</h2>
        <p className="text-sm text-text-muted">Tenant'ın tüm eş zamanlı yüklemelerinin toplamı için üst sınır.</p>
        <div className="flex flex-wrap items-center justify-between gap-3">
          <p className="text-lg" data-testid="tenant-limit"><Limit value={data?.tenant.bytesPerSecond} /></p>
          {edit({ scope: 'tenant' }, tenant.name, data?.tenant.bytesPerSecond)}
        </div>
      </section>
      <section aria-labelledby="bandwidth-agents-heading" className="space-y-3">
        <h2 id="bandwidth-agents-heading" className="font-medium text-text-heading">Agent sınırları</h2>
        <QueryState isPending={agents.isPending} error={agents.error}><DataTable rows={agents.data ?? []} columns={agentColumns} keyExtractor={agent => agent.agentId} emptyMessage="Agent bulunamadı." /></QueryState>
      </section>
      <section aria-labelledby="bandwidth-jobs-heading" className="space-y-3">
        <h2 id="bandwidth-jobs-heading" className="font-medium text-text-heading">Backup Job sınırları</h2>
        <QueryState isPending={jobs.isPending} error={jobs.error}><DataTable rows={jobs.data ?? []} columns={jobColumns} keyExtractor={job => job.id} emptyMessage="Backup Job bulunamadı." /></QueryState>
      </section>
    </QueryState>
    {editing && data && <LimitDialog {...editing} minimum={data.minimumBytesPerSecond} maximum={data.maximumBytesPerSecond} onClose={() => setEditing(null)} />}
  </div>
}
