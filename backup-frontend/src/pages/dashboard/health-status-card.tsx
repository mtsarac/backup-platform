import { tx, useLanguage } from '@/lib/i18n'
import { Link } from 'react-router-dom'
import { ChevronRight, DatabaseBackup } from 'lucide-react'
import { QueryState } from '@/components/data/query-state'
import { StatusPill } from '@/components/data/status-pill'
import { buttonVariants } from '@/components/ui/button-variants'
import { EmptyState } from '@/components/ui/empty-state'
import { Section } from '@/components/ui/section'
import { ErrorNotice } from '@/components/ui/notice'
import type { BackupHealthState } from '@/features/backup-jobs/health'
import type { StatusLevel } from '@/lib/status'
import { useHealthSummary } from './health-summary'
import { recommendations, type Recommendation } from './recommendations'

function verdict(state: BackupHealthState): { level: StatusLevel; title: string; text: string } {
  switch (state) {
    case 'Healthy': return { level: 'safe', title: tx("Yedekleriniz güvende"), text: tx("Tüm yedekleme işleriniz sağlıklı görünüyor.") }
    case 'Degraded': return { level: 'attention', title: tx("Yedeklerinizden bazıları dikkat istiyor"), text: tx("Verileriniz korunuyor ama bazı işlerin ilgilenilmesi gerekiyor.") }
    case 'Critical': return { level: 'urgent', title: tx("Yedeklerinizde acil bir sorun var"), text: tx("En az bir yedekleme işi ciddi risk taşıyor; önce aşağıdakilere bakın.") }
  }
}

function RecommendationList({ items, base, label = tx("Öneriler") }: { items: Recommendation[]; base: string; label?: string }) {
  return items.length > 0 && <ul aria-label={label} className="mt-4 divide-y divide-border rounded-lg border border-border">
    {items.map(item => <li key={item.jobId}>
      <Link to={`${base}/backup-jobs/${item.jobId}/edit`} className="flex min-h-11 items-center justify-between gap-3 px-4 py-2.5 text-body hover:bg-surface-3">
        <span className="min-w-0 break-words"><span className="font-medium text-text-heading">{item.jobName}</span>: {item.message}</span>
        <ChevronRight aria-hidden="true" className="size-4 shrink-0 text-text-muted" />
      </Link>
    </li>)}
  </ul>
}

function Verdict({ items, state, base }: { items: Recommendation[]; state: BackupHealthState; base: string }) {
  const { level, title, text } = verdict(state)
  return <Section>
    <div role="status" className="space-y-1"><StatusPill level={level} className="text-sm" /><p className="pt-2 text-xl font-semibold text-text-heading">{title}</p><p className="text-body text-text-muted">{text}</p></div>
    <RecommendationList items={items} base={base} />
  </Section>
}

/** The page's one question: are my backups safe? The state is the backend's; recommendations come from each unhealthy job's weakest factor. */
export function HealthStatusCard({ tenantId, canOperate, replicationInsufficientLast24Hours = 0 }: { tenantId: string; canOperate: boolean; replicationInsufficientLast24Hours?: number }) {
  useLanguage()
  const query = useHealthSummary(tenantId)
  const base = `/tenants/${tenantId}`
  const summary = query.data
  const items = recommendations(summary?.jobs ?? [])
  const replicationItems = items.filter(item => item.reason === 'replicas_missing' || item.reason === 'no_verified_replica')
  const otherItems = items.filter(item => !replicationItems.includes(item))
  return <div className="space-y-5">
    <QueryState isPending={query.isPending} error={query.error} onRetry={() => void query.refetch()}>
      {summary && (summary.state === null
        ? <Section><div role="status"><EmptyState icon={DatabaseBackup} canAct={canOperate} message={tx("Henüz yedekleme kurulmadı")}
            action={<Link to={`${base}/backup-jobs/new`} className={buttonVariants()}>{tx("İlk yedeğinizi oluşturun")}</Link>} /></div></Section>
        : <Verdict items={otherItems} state={summary.state} base={base} />)}
    </QueryState>
    {(replicationItems.length > 0 || replicationInsufficientLast24Hours > 0) && <Section title={tx("Doğrulanmış kopyalar")}>
      {replicationInsufficientLast24Hours > 0 && <ErrorNotice what={tx("Son 24 saatte {0} yedekleme kullanılabilir yedek oluşturdu, ancak gereken doğrulanmış kopya sayısına ulaşamadı.", { "0": replicationInsufficientLast24Hours })} dataImpact={tx("Yedekler kullanılabilir durumda ama gereken kopya sayısına ulaşılamadı.")} action={tx("Eksik kopyaları onarmak için sonuçları inceleyin.")} control={<Link className="font-medium underline" to={`${base}/backup-runs?status=Succeeded`}>{tx("Replikasyon sonuçlarını inceleyin.")}</Link>} />}
      <RecommendationList items={replicationItems} base={base} label={tx("Doğrulanmış kopyalar")} />
    </Section>}
  </div>
}
