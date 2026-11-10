import { tx, useLanguage } from '@/lib/i18n'
import { ChevronRight } from 'lucide-react'
import { Link } from 'react-router-dom'
import { KpiCard } from '@/components/data/kpi-card'
import { StatusPill } from '@/components/data/status-pill'
import { PageHeader } from '@/components/ui/page-header'
import { usePlatformSummary } from '@/features/platform/hooks'
import { platformPaths } from '@/features/platform/paths'
import { DashboardStorage } from '@/features/platform/storage/dashboard-storage'
import type { PlatformSummary } from '@/features/platform/types'
import { QueryState } from '@/components/data/query-state'
import { platformOnlyMessage } from '@/lib/api/failure-messages'

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
    <QueryState forbiddenMessage={platformOnlyMessage} isPending={query.isPending} error={query.error}>
      {data && <>
        <DashboardAttention data={data} />
        <div className="grid gap-x-6 gap-y-4 grid-cols-2 xl:grid-cols-4">
          <Link to={platformPaths.tenants} className="block rounded-lg"><KpiCard label={tx("Çalışma alanları")} value={data.tenants} className="h-full hover:bg-surface-2" /></Link>
          <Link to={platformPaths.users} className="block rounded-lg"><KpiCard label={tx("Kullanıcılar")} value={data.users} className="h-full hover:bg-surface-2" /></Link>
          <Link to={platformPaths.agents()} className="block rounded-lg"><KpiCard label={tx("Yedekleme bilgisayarları")} value={data.agents.total} hint={tx("{0} çevrimiçi · {1} iptal edilmiş", { "0": data.agents.online, "1": data.agents.revoked })} className="h-full hover:bg-surface-2" /></Link>
          <KpiCard label={tx("Etkin yedekleme işleri")} value={data.enabledBackupJobs} />
        </div>
        <DashboardStorage />
      </>}
    </QueryState>
  </div>
}

function DashboardAttention({ data }: { data: PlatformSummary }) {
  useLanguage()
  const items = attentionItems(data)
  return <section>
    <div role="status" className="flex flex-wrap items-center gap-x-3 gap-y-1">
      <StatusPill level={items.length > 0 ? 'attention' : 'safe'} />
      <h2 className="text-base font-semibold text-text-heading">{items.length > 0 ? tx("{0} konu ilgi bekliyor", { "0": items.length }) : tx("Dikkat gerektiren bir durum yok.")}</h2>
    </div>
    {items.length > 0 && <ul aria-label={tx("Dikkat gerektirenler")} className="mt-3 divide-y divide-border rounded-lg border border-border bg-surface">
      {items.map((item) => <li key={item.key}>
        <Link className="flex min-h-9 pointer-coarse:min-h-11 items-center justify-between gap-3 px-4 py-2.5 text-body text-primary hover:bg-surface-3" to={item.to}>
          <span className="min-w-0 break-words">{item.text}</span>
          <ChevronRight aria-hidden="true" className="size-4 shrink-0" />
        </Link>
      </li>)}
    </ul>}
  </section>
}
