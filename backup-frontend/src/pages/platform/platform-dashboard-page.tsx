import { tx, useLanguage } from '@/lib/i18n'
import { AlertCircle, Building2, ChevronRight, Database, Monitor, Users } from 'lucide-react'
import { Link } from 'react-router-dom'
import { KpiCard } from '@/components/data/kpi-card'
import { StatusPill } from '@/components/data/status-pill'
import { PageHeader } from '@/components/ui/page-header'
import { Section } from '@/components/ui/section'
import { AgentBreakdown } from '@/features/platform/agent-breakdown'
import { usePlatformSummary } from '@/features/platform/hooks'
import { platformPaths } from '@/features/platform/paths'
import { PlatformQueryState } from '@/features/platform/platform-query-state'
import { DashboardStorage } from '@/features/platform/storage/dashboard-storage'
import { linkClass } from '@/components/ui/form-styles'
import type { PlatformSummary } from '@/features/platform/types'

interface Attention { key: string; text: string; to: string }

/** Only what needs a decision: counts of zero produce no row. */
function attentionItems(data: PlatformSummary): Attention[] {
  const items: Attention[] = []
  if (data.failedRunsLast24Hours > 0) items.push({ key: 'runs', text: tx("Son 24 saatte {0} yedekleme denemesi başarısız oldu.", { "0": data.failedRunsLast24Hours }), to: platformPaths.audit() })
  if (data.agents.offline > 0) items.push({ key: 'offline', text: tx("{0} yedekleme bilgisayarı çevrimdışı.", { "0": data.agents.offline }), to: `${platformPaths.agents()}?connectivity=offline` })
  if (data.agents.unknown > 0) items.push({ key: 'unknown', text: tx("{0} yedekleme bilgisayarının bağlantı durumu bilinmiyor.", { "0": data.agents.unknown }), to: `${platformPaths.agents()}?connectivity=unknown` })
  if (data.agents.disabled > 0) items.push({ key: 'disabled', text: tx("{0} yedekleme bilgisayarı devre dışı bırakılmış.", { "0": data.agents.disabled }), to: `${platformPaths.agents()}?status=Disabled` })
  return items
}

export function PlatformDashboardPage() {
  useLanguage()
  const query = usePlatformSummary()
  const data = query.data
  return <div className="space-y-6">
    <PageHeader title={tx("Platform özeti")} description={tx("Tüm çalışma alanlarının genel durumu.")} />
    <PlatformQueryState isPending={query.isPending} error={query.error}>
      {data && <>
        <DashboardAttention data={data} />
        <details className="rounded-xl border border-border bg-surface shadow-card">
          <summary className="min-h-11 cursor-pointer select-none rounded-xl px-5 py-3 font-semibold text-text-heading">{tx("Ayrıntılar")}</summary>
          <div className="space-y-6 border-t border-border p-5">
            <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-5">
              <Link to={platformPaths.tenants} className="block rounded-lg"><KpiCard label={tx("Çalışma alanları")} value={data.tenants} icon={<Building2 className="size-4" />} className="h-full hover:bg-surface-2" /></Link>
              <Link to={platformPaths.users} className="block rounded-lg"><KpiCard label={tx("Kullanıcılar")} value={data.users} icon={<Users className="size-4" />} className="h-full hover:bg-surface-2" /></Link>
              <Link to={platformPaths.agents()} className="block rounded-lg"><KpiCard label={tx("Yedekleme bilgisayarları")} value={data.agents.total} icon={<Monitor className="size-4" />} className="h-full hover:bg-surface-2" /></Link>
              <KpiCard label={tx("Etkin yedekleme işleri")} value={data.enabledBackupJobs} icon={<Database className="size-4" />} />
              <KpiCard label={tx("Başarısız yedeklemeler · son 24 saat")} value={data.failedRunsLast24Hours} tone={data.failedRunsLast24Hours > 0 ? 'error' : undefined} icon={<AlertCircle className="size-4" />} className={data.failedRunsLast24Hours > 0 ? 'border-error/40' : undefined} />
            </div>
            <Section title={tx("Yedekleme bilgisayarlarının durumu")} description={tx("Bağlantı kesilmesi ile devre dışı bırakma ayrı durumlardır.")} actions={<Link className={`${linkClass} inline-flex min-h-11 items-center text-sm`} to={platformPaths.agents()}>{tx("Tüm yedekleme bilgisayarları")}</Link>}>
              <AgentBreakdown counts={data.agents} />
            </Section>
            <DashboardStorage />
          </div>
        </details>
      </>}
    </PlatformQueryState>
  </div>
}

function DashboardAttention({ data }: { data: PlatformSummary }) {
  useLanguage()
  const items = attentionItems(data)
  return <Section title={tx("Platform sağlıklı mı?")}>
    <div role="status" className="space-y-2">
      <StatusPill level={items.length > 0 ? 'attention' : 'safe'} />
      <p className="text-[0.9375rem] text-text-muted">{items.length > 0 ? tx("Önce bunlara bakın.") : tx("Dikkat gerektiren bir durum yok.")}</p>
    </div>
    {items.length > 0 && <ul aria-label={tx("Dikkat gerektirenler")} className="mt-4 divide-y divide-border rounded-lg border border-border">
      {items.slice(0, 3).map((item) => <li key={item.key}>
        <Link className="flex min-h-11 items-center justify-between gap-3 px-4 py-2.5 text-[0.9375rem] text-primary hover:bg-surface-3" to={item.to}>
          <span className="min-w-0 break-words">{item.text}</span>
          <ChevronRight aria-hidden="true" className="size-4 shrink-0" />
        </Link>
      </li>)}
    </ul>}
  </Section>
}
