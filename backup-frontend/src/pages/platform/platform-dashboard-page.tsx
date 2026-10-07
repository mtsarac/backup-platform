import { AlertCircle, Building2, Database, Monitor, Users } from 'lucide-react'
import { KpiCard } from '@/components/data/kpi-card'
import { AgentBreakdown } from '@/features/platform/agent-breakdown'
import { usePlatformSummary } from '@/features/platform/hooks'
import { PlatformQueryState } from '@/features/platform/platform-query-state'

export function PlatformDashboardPage() {
  const query = usePlatformSummary()
  const data = query.data
  return <div className="space-y-6">
    <div><h1 className="text-2xl font-semibold text-text-heading">Platform Dashboard</h1><p className="mt-1 text-sm text-text-muted">Tüm tenant'lar genelinde özet durum.</p></div>
    <PlatformQueryState isPending={query.isPending} error={query.error}>
      {data && <>
        <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-5">
          <KpiCard label="Tenants" value={data.tenants} icon={<Building2 className="size-4" />} />
          <KpiCard label="Users" value={data.users} icon={<Users className="size-4" />} />
          <KpiCard label="Agents" value={data.agents.total} icon={<Monitor className="size-4" />} />
          <KpiCard label="Etkin Backup Jobs" value={data.enabledBackupJobs} icon={<Database className="size-4" />} />
          <KpiCard label="Başarısız Backup Runs · son 24 saat" value={data.failedRunsLast24Hours} tone={data.failedRunsLast24Hours > 0 ? 'error' : undefined} icon={<AlertCircle className="size-4" />} className={data.failedRunsLast24Hours > 0 ? 'border-error/40' : undefined} />
        </div>
        <section className="space-y-3 rounded-lg border border-border bg-surface p-4"><h2 className="font-medium text-text-heading">Agent bağlantı durumu ve yaşam döngüsü</h2><AgentBreakdown counts={data.agents} /></section>
      </>}
    </PlatformQueryState>
  </div>
}
