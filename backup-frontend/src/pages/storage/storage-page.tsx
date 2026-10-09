import { RefreshCw, ShieldAlert } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { QueryState } from '@/components/data/query-state'
import { agentDate } from '@/features/agents/format'
import { useTenantStorageQuota } from '@/features/storage/hooks'
import { accountingModeHelp, accountingModeLabels, enforcementModeHelp, enforcementModeLabels, isLegacyUnlimited, scopeLabels } from '@/features/storage/labels'
import { EntitlementStatusBadge, PlanName } from '@/features/storage/quota-badges'
import { Fact, QuotaSummary, StandingBadge, StorageNotices } from '@/features/storage/quota-summary'
import { formatCapacity } from '@/features/storage/format'
import { ApiError } from '@/lib/api/client'
import { useTenant } from '@/features/tenancy/use-tenant'
import { TenantDestinations } from '@/features/storage-connections/tenant-destinations'

function Forbidden() {
  return <div role="alert" className="space-y-2 rounded-lg border border-error/30 bg-error-subtle p-6 text-error">
    <div className="flex items-center gap-2"><ShieldAlert className="size-5" aria-hidden="true" /><h2 className="font-medium">Bu tenant'ın depolama bilgisine erişiminiz yok</h2></div>
    <p className="text-sm">Depolama kullanımını yalnızca bu tenant'ın üyeleri görebilir.</p>
  </div>
}

export function StoragePage() {
  const { tenant } = useTenant()
  const query = useTenantStorageQuota()
  const quota = query.data
  const forbidden = query.error instanceof ApiError && query.error.status === 403
  return <div className="space-y-6">
    <div className="flex flex-wrap items-start justify-between gap-3">
      <div><h1 className="text-2xl font-semibold text-text-heading">Storage</h1><p className="mt-1 text-sm text-text-muted">{tenant.name} · Depolama planı, kullanım, kota durumu ve depolama hedefleri. 1 GB = 1.000.000.000 bayt.</p></div>
      <Button variant="outline" onClick={() => { void query.refetch() }} disabled={query.isFetching}><RefreshCw className={`size-4 ${query.isFetching ? 'animate-spin' : ''}`} aria-hidden="true" />{query.isFetching && !query.isPending ? 'Yenileniyor…' : 'Yenile'}</Button>
    </div>
    {forbidden ? <Forbidden /> : !quota ? <QueryState isPending={query.isPending} error={query.error} /> : <>
        {query.isError && <p role="alert" className="rounded-lg border border-warning/30 bg-warning-subtle p-3 text-sm text-warning">Güncel veri alınamadı; son başarılı okuma gösteriliyor ({agentDate(quota.calculatedAt)} hesaplaması).</p>}
        <StorageNotices quota={quota} />
        <section className="space-y-4 rounded-lg border border-border bg-surface p-4" aria-labelledby="storage-plan-heading">
          <h2 id="storage-plan-heading" className="font-medium text-text-heading">Plan ve abonelik</h2>
          <dl className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
            <Fact label="Aktif plan"><PlanName plan={quota.plan} /></Fact>
            <Fact label="Abonelik durumu"><EntitlementStatusBadge status={quota.subscriptionStatus} /></Fact>
            <Fact label="Kota durumu"><StandingBadge quota={quota} /></Fact>
            <Fact label="Yeni backup">{quota.newBackupsAllowed ? 'Başlatılabilir' : 'Başlatılamaz'}</Fact>
            {quota.plan?.subscriptionEndsAt && <Fact label="Abonelik bitişi">{agentDate(quota.plan.subscriptionEndsAt)}</Fact>}
          </dl>
        </section>
        <section className="space-y-4 rounded-lg border border-border bg-surface p-4" aria-labelledby="storage-usage-heading">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <h2 id="storage-usage-heading" className="font-medium text-text-heading">Kullanım</h2>
            {quota.limitBytes !== null && <span className="text-sm text-text-muted">Plan limiti: {formatCapacity(quota.limitBytes)}</span>}
          </div>
          <QuotaSummary quota={quota} />
        </section>
        <section className="space-y-4 rounded-lg border border-border bg-surface p-4" aria-labelledby="storage-accounting-heading">
          <h2 id="storage-accounting-heading" className="font-medium text-text-heading">Nasıl hesaplanır</h2>
          <dl className="grid gap-4 lg:grid-cols-2">
            <Fact label="Hesaplama modu" hint={accountingModeHelp[quota.accountingMode]}>{accountingModeLabels[quota.accountingMode]}</Fact>
            <Fact label="Kotaya dahil storage kapsamı" hint="Yalnızca bu kapsamdaki kopyalar kullanıma sayılır.">
              <ul className="list-inside list-disc">{scopeLabels(quota.includedStorageScopes).map((label) => <li key={label}>{label}</li>)}</ul>
            </Fact>
            <Fact label="Kota uygulaması" hint={enforcementModeHelp[quota.enforcementMode]}>{enforcementModeLabels[quota.enforcementMode]}</Fact>
            <Fact label="Eşikler">Warning %{quota.warningThreshold} · Critical %{quota.criticalThreshold}</Fact>
          </dl>
          {isLegacyUnlimited(quota.plan) && <p className="text-xs text-text-muted">Legacy Unlimited planda limit olmadığı için eşikler uygulanmaz.</p>}
        </section>
    </>}
    {!forbidden && !query.isPending && <TenantDestinations />}
  </div>
}
