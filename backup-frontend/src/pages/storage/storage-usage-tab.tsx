import { RefreshCw, ShieldAlert } from 'lucide-react'
import { QueryState } from '@/components/data/query-state'
import { Button } from '@/components/ui/button'
import { Notice } from '@/components/ui/notice'
import { Section } from '@/components/ui/section'
import { agentDate } from '@/features/agents/format'
import { formatCapacity } from '@/features/storage/format'
import { useTenantStorageQuota } from '@/features/storage/hooks'
import { accountingModeHelp, accountingModeLabels, enforcementModeHelp, enforcementModeLabels, isLegacyUnlimited, scopeLabels } from '@/features/storage/labels'
import { EntitlementStatusBadge, PlanName } from '@/features/storage/quota-badges'
import { Fact, QuotaSummary, StandingBadge, StorageNotices } from '@/features/storage/quota-summary'
import { ApiError } from '@/lib/api/client'

function Forbidden() {
  return <Notice tone="error" title="Bu tenant'ın depolama bilgisine erişiminiz yok"><ShieldAlert className="sr-only" aria-hidden="true" />Depolama kullanımını yalnızca bu tenant'ın üyeleri görebilir.</Notice>
}

export function StorageUsageTab() {
  const query = useTenantStorageQuota()
  const quota = query.data
  const forbidden = query.error instanceof ApiError && query.error.status === 403
  if (forbidden) return <Forbidden />
  if (!quota) return <QueryState isPending={query.isPending} error={query.error} />
  return <div className="space-y-5">
    {query.isError && <Notice tone="warning">Güncel veri alınamadı; son başarılı okuma gösteriliyor ({agentDate(quota.calculatedAt)} hesaplaması).</Notice>}
    <StorageNotices quota={quota} />
    <Section title="Kullanım" description={quota.limitBytes !== null ? `Plan limiti: ${formatCapacity(quota.limitBytes)}. 1 GB = 1.000.000.000 bayt.` : '1 GB = 1.000.000.000 bayt.'}
      actions={<Button variant="outline" size="sm" onClick={() => { void query.refetch() }} disabled={query.isFetching}><RefreshCw className={`size-4 ${query.isFetching ? 'animate-spin' : ''}`} aria-hidden="true" />{query.isFetching && !query.isPending ? 'Yenileniyor…' : 'Yenile'}</Button>}>
      <QuotaSummary quota={quota} />
    </Section>
    <Section title="Plan ve abonelik">
      <dl className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
        <Fact label="Aktif plan"><PlanName plan={quota.plan} /></Fact>
        <Fact label="Abonelik durumu"><EntitlementStatusBadge status={quota.subscriptionStatus} /></Fact>
        <Fact label="Kota durumu"><StandingBadge quota={quota} /></Fact>
        <Fact label="Yeni backup">{quota.newBackupsAllowed ? 'Başlatılabilir' : 'Başlatılamaz'}</Fact>
        {quota.plan?.subscriptionEndsAt && <Fact label="Abonelik bitişi">{agentDate(quota.plan.subscriptionEndsAt)}</Fact>}
      </dl>
    </Section>
    <Section title="Nasıl hesaplanır">
      <dl className="grid gap-4 lg:grid-cols-2">
        <Fact label="Hesaplama modu" hint={accountingModeHelp[quota.accountingMode]}>{accountingModeLabels[quota.accountingMode]}</Fact>
        <Fact label="Kotaya dahil storage kapsamı" hint="Yalnızca bu kapsamdaki kopyalar kullanıma sayılır.">
          <ul className="list-inside list-disc">{scopeLabels(quota.includedStorageScopes).map((label) => <li key={label}>{label}</li>)}</ul>
        </Fact>
        <Fact label="Kota uygulaması" hint={enforcementModeHelp[quota.enforcementMode]}>{enforcementModeLabels[quota.enforcementMode]}</Fact>
        <Fact label="Eşikler">Warning %{quota.warningThreshold} · Critical %{quota.criticalThreshold}</Fact>
      </dl>
      {isLegacyUnlimited(quota.plan) && <p className="mt-3 text-sm text-text-muted">Legacy Unlimited planda limit olmadığı için eşikler uygulanmaz.</p>}
    </Section>
  </div>
}
