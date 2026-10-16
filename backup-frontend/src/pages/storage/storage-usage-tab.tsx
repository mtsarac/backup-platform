import { tx, useLanguage } from '@/lib/i18n'
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
  useLanguage()
  return <Notice tone="error" title={tx("Bu tenant'ın depolama bilgisine erişiminiz yok")}><ShieldAlert className="sr-only" aria-hidden="true" />{tx("Depolama kullanımını yalnızca bu tenant'ın üyeleri görebilir.")}</Notice>
}

export function StorageUsageTab() {
  useLanguage()
  const query = useTenantStorageQuota()
  const quota = query.data
  const forbidden = query.error instanceof ApiError && query.error.status === 403
  if (forbidden) return <Forbidden />
  if (!quota) return <QueryState isPending={query.isPending} error={query.error} />
  return <div className="space-y-5">
    {query.isError && <Notice tone="warning">{tx("Güncel veri alınamadı; son başarılı okuma gösteriliyor (")}{agentDate(quota.calculatedAt)}{' '}{tx("hesaplaması).")}</Notice>}
    <StorageNotices quota={quota} />
    <Section title={tx("Kullanım")} description={tx("En küçük birim MB.")}
      actions={<Button variant="outline" size="sm" onClick={() => { void query.refetch() }} disabled={query.isFetching}><RefreshCw className={`size-4 ${query.isFetching ? 'animate-spin' : ''}`} aria-hidden="true" />{query.isFetching && !query.isPending ? 'Yenileniyor…' : tx('Yenile')}</Button>}>
      <QuotaSummary quota={quota} />
    </Section>
    <Section title={tx("Plan ve abonelik")}>
      <dl className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
        <Fact label={tx("Aktif plan")}><PlanName plan={quota.plan} /></Fact>
        <Fact label={tx("Abonelik durumu")}><EntitlementStatusBadge status={quota.subscriptionStatus} /></Fact>
        <Fact label={tx("Kota durumu")}><StandingBadge quota={quota} /></Fact>
        <Fact label={tx("Yeni backup")}>{quota.newBackupsAllowed ? tx('Başlatılabilir') : tx('Başlatılamaz')}</Fact>
        {quota.plan?.subscriptionEndsAt && <Fact label={tx("Abonelik bitişi")}>{agentDate(quota.plan.subscriptionEndsAt)}</Fact>}
      </dl>
    </Section>
    <Section title={tx("Nasıl hesaplanır")}>
      <dl className="grid gap-4 lg:grid-cols-2">
        <Fact label={tx("Hesaplama modu")} hint={accountingModeHelp[quota.accountingMode]}>{accountingModeLabels[quota.accountingMode]}</Fact>
        <Fact label={tx("Kotaya dahil storage kapsamı")} hint={tx("Yalnızca bu kapsamdaki kopyalar kullanıma sayılır.")}>
          <ul className="list-inside list-disc">{scopeLabels(quota.includedStorageScopes).map((label) => <li key={label}>{tx(label)}</li>)}</ul>
        </Fact>
        <Fact label={tx("Kota uygulaması")} hint={enforcementModeHelp[quota.enforcementMode]}>{enforcementModeLabels[quota.enforcementMode]}</Fact>
        <Fact label={tx("Eşikler")}>{tx("Warning %")}{quota.warningThreshold}{' '}{tx("· Critical %")}{quota.criticalThreshold}</Fact>
      </dl>
      {isLegacyUnlimited(quota.plan) && <p className="mt-3 text-sm text-text-muted">{tx("Legacy Unlimited planda limit olmadığı için eşikler uygulanmaz.")}</p>}
    </Section>
  </div>
}
