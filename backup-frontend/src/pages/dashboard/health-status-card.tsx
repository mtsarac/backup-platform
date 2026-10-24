import { tx, useLanguage } from '@/lib/i18n'
import { Link } from 'react-router-dom'
import { ChevronRight, DatabaseBackup } from 'lucide-react'
import { QueryState } from '@/components/data/query-state'
import { StatusPill } from '@/components/data/status-pill'
import { buttonVariants } from '@/components/ui/button-variants'
import { EmptyState } from '@/components/ui/empty-state'
import { Section } from '@/components/ui/section'
import type { BackupHealthState } from '@/features/backup-jobs/health'
import type { StatusLevel } from '@/lib/status'
import { useHealthSummary, type HealthSummary } from './health-summary'
import { recommendations } from './recommendations'

function verdict(state: BackupHealthState): { level: StatusLevel; title: string; text: string } {
  switch (state) {
    case 'Healthy': return { level: 'safe', title: tx("Yedekleriniz güvende"), text: tx("Tüm yedekleme işleriniz sağlıklı görünüyor.") }
    case 'Degraded': return { level: 'attention', title: tx("Yedeklerinizden bazıları dikkat istiyor"), text: tx("Verileriniz korunuyor ama bazı işlerin ilgilenilmesi gerekiyor.") }
    case 'Critical': return { level: 'urgent', title: tx("Yedeklerinizde acil bir sorun var"), text: tx("En az bir yedekleme işi ciddi risk taşıyor; önce aşağıdakilere bakın.") }
  }
}

function Verdict({ summary, state, base }: { summary: HealthSummary; state: BackupHealthState; base: string }) {
  const { level, title, text } = verdict(state)
  const items = recommendations(summary.jobs)
  return <Section>
    <div role="status" className="space-y-1"><StatusPill level={level} className="text-sm" /><p className="pt-2 text-xl font-semibold text-text-heading">{title}</p><p className="text-[0.9375rem] text-text-muted">{text}</p></div>
    {items.length > 0 && <ul aria-label={tx("Öneriler")} className="mt-4 divide-y divide-border rounded-lg border border-border">
      {items.map(item => <li key={item.jobId}>
        <Link to={`${base}/backup-jobs/${item.jobId}/edit`} className="flex min-h-11 items-center justify-between gap-3 px-4 py-2.5 text-[0.9375rem] hover:bg-surface-3">
          <span className="min-w-0 break-words"><span className="font-medium text-text-heading">{item.jobName}</span>: {item.message}</span>
          <ChevronRight aria-hidden="true" className="size-4 shrink-0 text-text-muted" />
        </Link>
      </li>)}
    </ul>}
  </Section>
}

/** The page's one question: are my backups safe? The state is the backend's; recommendations come from each unhealthy job's weakest factor. */
export function HealthStatusCard({ tenantId, canAdmin }: { tenantId: string; canAdmin: boolean }) {
  useLanguage()
  const query = useHealthSummary(tenantId)
  const base = `/tenants/${tenantId}`
  const summary = query.data
  return <QueryState isPending={query.isPending} error={query.error} onRetry={() => void query.refetch()}>
    {summary && (summary.state === null
      ? <Section><div role="status"><EmptyState icon={DatabaseBackup} canAct={canAdmin} message={tx("Henüz yedekleme kurulmadı")}
          action={<Link to={`${base}/backup-jobs/new`} className={buttonVariants()}>{tx("İlk yedeğinizi oluşturun")}</Link>} /></div></Section>
      : <Verdict summary={summary} state={summary.state} base={base} />)}
  </QueryState>
}
