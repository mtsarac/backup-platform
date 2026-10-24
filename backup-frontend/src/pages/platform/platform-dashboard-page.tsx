import { tx, useLanguage } from '@/lib/i18n'
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
import { linkClass } from '@/components/ui/form-styles'
import type { PlatformSummary } from '@/features/platform/types'

interface Attention { key: string; text: string; to: string; action: string }

/** Only what needs a decision: counts of zero produce no row. */
function attentionItems(data: PlatformSummary): Attention[] {
  const items: Attention[] = []
  if (data.failedRunsLast24Hours > 0) items.push({ key: 'runs', text: tx("Son 24 saatte {0} Backup Run başarısız oldu.", { "0": data.failedRunsLast24Hours }), to: platformPaths.audit(), action: tx('Audit kayıtlarını aç') })
  if (data.agents.offline > 0) items.push({ key: 'offline', text: tx("{0} Agent çevrimdışı.", { "0": data.agents.offline }), to: `${platformPaths.agents()}?connectivity=offline`, action: tx('Çevrimdışı Agent\'ları listele') })
  if (data.agents.unknown > 0) items.push({ key: 'unknown', text: tx("{0} Agent'ın bağlantı durumu bilinmiyor.", { "0": data.agents.unknown }), to: `${platformPaths.agents()}?connectivity=unknown`, action: tx('Bilinmeyen Agent\'ları listele') })
  if (data.agents.disabled > 0) items.push({ key: 'disabled', text: tx("{0} Agent devre dışı bırakılmış.", { "0": data.agents.disabled }), to: `${platformPaths.agents()}?status=Disabled`, action: tx('Devre dışı Agent\'ları listele') })
  return items
}

export function PlatformDashboardPage() {
  useLanguage()
  const query = usePlatformSummary()
  const data = query.data
  return <div className="space-y-6">
    <PageHeader title={tx("Platform Dashboard")} description={tx("Tüm tenant'lar genelinde özet durum.")} />
    <PlatformQueryState isPending={query.isPending} error={query.error}>
      {data && <>
        <DashboardAttention data={data} />
        <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-5">
          <Link to={platformPaths.tenants} className="block rounded-lg"><KpiCard label={tx("Tenants")} value={data.tenants} icon={<Building2 className="size-4" />} className="h-full hover:bg-surface-2" /></Link>
          <Link to={platformPaths.users} className="block rounded-lg"><KpiCard label={tx("Users")} value={data.users} icon={<Users className="size-4" />} className="h-full hover:bg-surface-2" /></Link>
          <Link to={platformPaths.agents()} className="block rounded-lg"><KpiCard label={tx("Agents")} value={data.agents.total} icon={<Monitor className="size-4" />} className="h-full hover:bg-surface-2" /></Link>
          <KpiCard label={tx("Etkin Backup Jobs")} value={data.enabledBackupJobs} icon={<Database className="size-4" />} />
          <KpiCard label={tx("Başarısız Backup Runs · son 24 saat")} value={data.failedRunsLast24Hours} tone={data.failedRunsLast24Hours > 0 ? 'error' : undefined} icon={<AlertCircle className="size-4" />} className={data.failedRunsLast24Hours > 0 ? 'border-error/40' : undefined} />
        </div>
        <Section title={tx("Agent bağlantı durumu ve yaşam döngüsü")} description={tx("Bağlantı kesilmesi ile devre dışı bırakma ayrı durumlardır.")} actions={<Link className={`${linkClass} text-sm`} to={platformPaths.agents()}>{tx("Tüm Agent'lar")}</Link>}>
          <AgentBreakdown counts={data.agents} />
        </Section>
        <DashboardStorage />
      </>}
    </PlatformQueryState>
  </div>
}

function DashboardAttention({ data }: { data: PlatformSummary }) {
  useLanguage()
  const items = attentionItems(data)
  if (items.length === 0) return <Notice tone="success" title={tx("Dikkat gerektiren bir durum yok.")}>{tx("Son 24 saatte başarısız Backup Run yok; çevrimdışı, bağlantısı bilinmeyen veya devre dışı Agent yok.")}</Notice>
  return <Section title={tx("Dikkat gerektirenler")} description={tx("Önce bunlara bakın.")}>
    <ul className="divide-y divide-border">
      {items.map((item) => <li key={item.key} className="flex flex-wrap items-center justify-between gap-2 py-2 first:pt-0 last:pb-0">
        <span className="text-[0.9375rem]">{item.text}</span>
        <Link className={`${linkClass} text-sm`} to={item.to}>{item.action}</Link>
      </li>)}
    </ul>
  </Section>
}
