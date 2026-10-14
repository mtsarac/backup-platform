import { AlertCircle, Building2, Database, Monitor, Users } from 'lucide-react'
import { Link } from 'react-router-dom'
import { KpiCard } from '@/components/data/kpi-card'
import { Notice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { Section } from '@/components/ui/section'
import { AgentBreakdown } from '@/features/platform/agent-breakdown'
import { usePlatformSummary } from '@/features/platform/hooks'
import { platformPaths } from '@/features/platform/paths'
import { PlatformQueryState } from '@/features/platform/platform-query-state'
import { DashboardStorage } from '@/features/platform/storage/dashboard-storage'
import { linkClass } from '@/features/platform/styles'
import type { PlatformSummary } from '@/features/platform/types'

interface Attention { key: string; text: string; to: string; action: string }

/** Only what needs a decision: counts of zero produce no row. */
function attentionItems(data: PlatformSummary): Attention[] {
  const items: Attention[] = []
  if (data.failedRunsLast24Hours > 0) items.push({ key: 'runs', text: `Son 24 saatte ${data.failedRunsLast24Hours} Backup Run başarısız oldu.`, to: platformPaths.audit(), action: 'Audit kayıtlarını aç' })
  if (data.agents.offline > 0) items.push({ key: 'offline', text: `${data.agents.offline} Agent çevrimdışı.`, to: `${platformPaths.agents()}?connectivity=offline`, action: 'Çevrimdışı Agent\'ları listele' })
  if (data.agents.unknown > 0) items.push({ key: 'unknown', text: `${data.agents.unknown} Agent'ın bağlantı durumu bilinmiyor.`, to: `${platformPaths.agents()}?connectivity=unknown`, action: 'Bilinmeyen Agent\'ları listele' })
  if (data.agents.disabled > 0) items.push({ key: 'disabled', text: `${data.agents.disabled} Agent devre dışı bırakılmış.`, to: `${platformPaths.agents()}?status=Disabled`, action: 'Devre dışı Agent\'ları listele' })
  return items
}

export function PlatformDashboardPage() {
  const query = usePlatformSummary()
  const data = query.data
  return <div className="space-y-6">
    <PageHeader title="Platform Dashboard" description="Tüm tenant'lar genelinde özet durum." />
    <PlatformQueryState isPending={query.isPending} error={query.error}>
      {data && <>
        <DashboardAttention data={data} />
        <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-5">
          <Link to={platformPaths.tenants} className="block rounded-lg"><KpiCard label="Tenants" value={data.tenants} icon={<Building2 className="size-4" />} className="h-full hover:bg-surface-2" /></Link>
          <Link to={platformPaths.users} className="block rounded-lg"><KpiCard label="Users" value={data.users} icon={<Users className="size-4" />} className="h-full hover:bg-surface-2" /></Link>
          <Link to={platformPaths.agents()} className="block rounded-lg"><KpiCard label="Agents" value={data.agents.total} icon={<Monitor className="size-4" />} className="h-full hover:bg-surface-2" /></Link>
          <KpiCard label="Etkin Backup Jobs" value={data.enabledBackupJobs} icon={<Database className="size-4" />} />
          <KpiCard label="Başarısız Backup Runs · son 24 saat" value={data.failedRunsLast24Hours} tone={data.failedRunsLast24Hours > 0 ? 'error' : undefined} icon={<AlertCircle className="size-4" />} className={data.failedRunsLast24Hours > 0 ? 'border-error/40' : undefined} />
        </div>
        <Section title="Agent bağlantı durumu ve yaşam döngüsü" description="Bağlantı kesilmesi ile devre dışı bırakma ayrı durumlardır." actions={<Link className={`${linkClass} text-sm`} to={platformPaths.agents()}>Tüm Agent'lar</Link>}>
          <AgentBreakdown counts={data.agents} />
        </Section>
        <DashboardStorage />
      </>}
    </PlatformQueryState>
  </div>
}

function DashboardAttention({ data }: { data: PlatformSummary }) {
  const items = attentionItems(data)
  if (items.length === 0) return <Notice tone="success" title="Dikkat gerektiren bir durum yok.">Son 24 saatte başarısız Backup Run yok; çevrimdışı, bağlantısı bilinmeyen veya devre dışı Agent yok.</Notice>
  return <Section title="Dikkat gerektirenler" description="Önce bunlara bakın.">
    <ul className="divide-y divide-border">
      {items.map((item) => <li key={item.key} className="flex flex-wrap items-center justify-between gap-2 py-2 first:pt-0 last:pb-0">
        <span className="text-[0.9375rem]">{item.text}</span>
        <Link className={`${linkClass} text-sm`} to={item.to}>{item.action}</Link>
      </li>)}
    </ul>
  </Section>
}
