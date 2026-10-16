import { currentLocale } from '@/lib/i18n'
import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { Link } from 'react-router-dom'
import { RefreshCw, ShieldCheck } from 'lucide-react'
import { DataTable, type Column } from '@/components/data/data-table'
import { Pagination } from '@/components/data/pagination'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { buttonVariants } from '@/components/ui/button-variants'
import { FilterField, Filters, filterControlClass } from '@/components/ui/filters'
import { Notice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { Section } from '@/components/ui/section'
import { platformCrumb } from '@/features/platform/crumbs'
import { bareTableClass } from '@/features/platform/list-empty'
import { ConfirmDialog } from '@/features/agents/confirm-dialog'
import { agentDate } from '@/features/agents/format'
import { formatCapacity, allocatedPercent } from '@/features/storage/format'
import { QuotaStateBadge, EntitlementStatusBadge, PlanName } from '@/features/storage/quota-badges'
import { QuotaMeter } from '@/features/storage/quota-meter'
import type { StorageUsageRow, QuotaState, EntitlementStatus } from '@/features/storage/types'
import { platformPaths } from '@/features/platform/paths'
import { PlatformQueryState } from '@/features/platform/platform-query-state'
import { SearchForm } from '@/features/platform/search-form'
import { linkClass } from '@/features/platform/styles'
import { PLATFORM_PAGE_SIZE, useListParams } from '@/features/platform/use-list-params'
import { errorMessage } from '@/lib/api/failure-messages'
import { useStorageUsage, useReconcileUsage, useRebuildUsage } from '@/features/platform/storage/hooks'

export function PlatformStoragePage() {
  useLanguage()
  const { page, get, update } = useListParams()
  const search = get('search')
  const quotaState = (get('quotaState') || undefined) as QuotaState | undefined
  const subscriptionStatus = (get('subscriptionStatus') || undefined) as EntitlementStatus | undefined

  const query = useStorageUsage({
    search,
    quotaState,
    subscriptionStatus,
    page,
    pageSize: PLATFORM_PAGE_SIZE,
  })

  const [activeReconcile, setActiveReconcile] = useState<StorageUsageRow | null>(null)
  const [activeRebuild, setActiveRebuild] = useState<StorageUsageRow | null>(null)
  const [statusMessage, setStatusMessage] = useState<string | null>(null)

  const reconcile = useReconcileUsage(activeReconcile?.tenantId ?? '')
  const rebuild = useRebuildUsage(activeRebuild?.tenantId ?? '')

  const data = query.data

  function runReconcile() {
    if (!activeReconcile) return
    setStatusMessage(null)
    reconcile.mutate(undefined, {
      onSuccess: (res) => {
        setActiveReconcile(null)
        setStatusMessage(
          tx("“{0}” için reconcile tamamlandı: {1}.", { "0": activeReconcile.tenantName, "1": res.drifted ? tx('sapma düzeltildi') : tx('sapma yok') }),
        )
      },
    })
  }

  function runRebuild() {
    if (!activeRebuild) return
    setStatusMessage(null)
    rebuild.mutate(undefined, {
      onSuccess: () => {
        setActiveRebuild(null)
        setStatusMessage(tx("“{0}” için kullanım baştan hesaplandı (Rebuild).", { "0": activeRebuild.tenantName }))
      },
    })
  }

  const columns: Column<StorageUsageRow>[] = [
    {
      key: 'tenant',
      header: tx('Tenant'),
      wrap: true,
      render: (row) => (
        <div>
          <Link className={linkClass} to={platformPaths.tenant(row.tenantId)}>
            {row.tenantName}
          </Link>
          {!row.hasUsage && (
            <div className="text-xs text-text-muted">{tx("Projeksiyon yok")}</div>
          )}
          {row.hasUsage && !row.usageCurrent && (
            <Badge tone="warning" className="mt-0.5 text-[10px]">{tx("Eski Politika")}</Badge>
          )}
        </div>
      ),
    },
    {
      key: 'plan',
      header: tx('Plan & Durum'),
      render: (row) => (
        <div className="space-y-1">
          <div><PlanName plan={row.plan} /></div>
          <div><EntitlementStatusBadge status={row.subscriptionStatus} /></div>
        </div>
      ),
    },
    {
      key: 'usage',
      header: tx('Kullanılan / Limit'),
      wrap: true,
      render: (row) => {
        if (!row.hasUsage || row.usedBytes === null) {
          return <span className="text-xs text-text-muted">—</span>
        }
        const used = row.usedBytes
        const reserved = row.reservedBytes ?? 0
        const committed = used + reserved
        const limit = row.limitBytes
        const percent = allocatedPercent(used, reserved, limit)

        return (
          <div className="min-w-0 space-y-1">
            <div className="text-sm tabular-nums">
              <span className="font-semibold text-text-heading">{formatCapacity(committed)}</span>
              {limit !== null && (
                <span className="text-text-muted"> / {formatCapacity(limit)}</span>
              )}
            </div>
            {limit !== null && (
              <QuotaMeter
                usedBytes={used}
                reservedBytes={reserved}
                limitBytes={limit}
                state={row.state ?? 'Normal'}
                label={tx("{0} doluluk oranı", { "0": row.tenantName })}
              />
            )}
            {percent !== null && (
              <div className="text-sm text-text-muted">{tx('%{0}', { '0': percent.toLocaleString(currentLocale()) })}{' '}{tx("ayrılmış")}</div>
            )}
          </div>
        )
      },
    },
    {
      key: 'state',
      header: tx('Kota Durumu'),
      render: (row) => (
        <div className="space-y-1">
          {row.state ? <QuotaStateBadge state={row.state} /> : <span className="text-xs text-text-muted">—</span>}
          {!row.newBackupsAllowed && (
            <div>
              <Badge tone="error" className="text-[10px]">
                {row.denialCode === 'quota_exceeded' ? tx('Kota Doldu') : tx('Engellendi')}
              </Badge>
            </div>
          )}
        </div>
      ),
    },
    {
      key: 'policy',
      header: tx('Politika'),
      render: (row) => (
        <div className="text-xs">
          <span className="font-medium">{row.policy.key}</span>
          <span className="text-text-muted"> (v{row.policy.version})</span>
        </div>
      ),
    },
    {
      key: 'calculatedAt',
      header: tx('Son Hesaplama'),
      render: (row) => (
        <span className="text-xs text-text-muted">
          {row.calculatedAt ? agentDate(row.calculatedAt) : '—'}
        </span>
      ),
    },
    {
      key: 'actions',
      header: tx('İşlemler'),
      render: (row) => (
        <div className="flex flex-wrap gap-1.5">
          <Button
            variant="outline"
            size="sm"
            onClick={() => {
              reconcile.reset()
              setActiveReconcile(row)
            }}
          >{tx("Reconcile")}</Button>
          <Button
            variant="outline"
            size="sm"
            onClick={() => {
              rebuild.reset()
              setActiveRebuild(row)
            }}
          >{tx("Rebuild")}</Button>
        </div>
      ),
    },
  ]

  return (
    <div className="space-y-6">
      <PageHeader crumbs={[platformCrumb]} title={tx("Depolama ve Kota Yönetimi")} description={tx("Tüm tenant'ların depolama kullanımını, rezerve alanlarını ve kota durumlarını izleyin.")} actions={<>
        <Button variant="outline" onClick={() => void query.refetch()} disabled={query.isFetching}><RefreshCw aria-hidden="true" className={`size-4 ${query.isFetching ? 'animate-spin motion-reduce:animate-none' : ''}`} />{tx("Yenile")}</Button>
        <Link to={platformPaths.quotaPolicies} className={buttonVariants({ variant: 'outline' })}><ShieldCheck aria-hidden="true" className="size-4" />{tx("Kota Politikaları")}</Link>
      </>} />
      {statusMessage && <Notice tone="success">{statusMessage}</Notice>}
      <Filters label={tx("Depolama kullanım filtreleri")} onClear={search || quotaState || subscriptionStatus ? () => update({ search: '', quotaState: '', subscriptionStatus: '' }) : undefined}>
        <SearchForm className="sm:col-span-2" label={tx("Tenant ara")} value={search} onSearch={(value) => update({ search: value })} placeholder={tx("Tenant adı...")} />
        <FilterField label={tx("Kota durumu")}><select className={filterControlClass} value={quotaState ?? ''} onChange={(event) => update({ quotaState: event.target.value })}>
          <option value="">{tx("Tümü")}</option><option value="Normal">Normal</option><option value="Warning">{tx("Warning")}</option><option value="Critical">{tx("Critical")}</option><option value="OverQuota">{tx("OverQuota")}</option><option value="Unlimited">{tx("Unlimited")}</option>
        </select></FilterField>
        <FilterField label={tx("Abonelik")}><select className={filterControlClass} value={subscriptionStatus ?? ''} onChange={(event) => update({ subscriptionStatus: event.target.value })}>
          <option value="">{tx("Tümü")}</option><option value="Active">{tx("Etkin")}</option><option value="Suspended">{tx("Askıda")}</option><option value="Expired">{tx("Süresi doldu")}</option><option value="NoSubscription">{tx("Abonelik yok")}</option>
        </select></FilterField>
      </Filters>

      <PlatformQueryState isPending={query.isPending} error={query.error}>
        {data && (
          <div className="space-y-4">
            <Section title={tx("Tenant kullanımı")} description={tx("Kullanılan ve rezerve alanın toplamı etkin limit ile karşılaştırılır. Reconcile ve Rebuild kullanım kayıtlarını günceller; Backup dosyalarını silmez.")} flush className="overflow-hidden">
              <DataTable className={bareTableClass} rows={data.items} columns={columns} keyExtractor={(row) => row.tenantId} emptyMessage={tx("Kriterlere uygun tenant kullanımı bulunamadı.")} />
            </Section>
            {data.totalCount > 0 && (
              <Pagination
                label={tx("Kullanım sayfaları")}
                page={page}
                pageSize={data.pageSize}
                totalCount={data.totalCount}
                disabled={query.isFetching}
                onPageChange={(next) => update({ page: next })}
              />
            )}
          </div>
        )}
      </PlatformQueryState>

      <ConfirmDialog
        open={Boolean(activeReconcile)}
        onClose={() => setActiveReconcile(null)}
        title={tx("Depolama eşitlemesi çalıştırılsın mı?")}
        description={
          activeReconcile
            ? tx("“{0}” için mevcut artifact ve replikalar taranacak, kullanım güncellenecek ve süresi geçmiş rezervasyonlar iptal edilecektir.", { "0": activeReconcile.tenantName })
            : ''
        }
        confirmLabel={tx("Uzlaştırmayı Çalıştır")}
        pending={reconcile.isPending}
        error={reconcile.error ? errorMessage(reconcile.error) : undefined}
        onConfirm={runReconcile}
      />

      <ConfirmDialog
        open={Boolean(activeRebuild)}
        onClose={() => setActiveRebuild(null)}
        title={tx("Depolama baştan hesaplansın mı?")}
        description={
          activeRebuild
            ? tx("“{0}” için kullanım kaydı baştan hesaplanacaktır. Mevcut veritabanı kayıtları geçerli kota politikasına göre yeniden taranır.", { "0": activeRebuild.tenantName })
            : ''
        }
        confirmLabel={tx("Yeniden Hesaplamayı Çalıştır")}
        pending={rebuild.isPending}
        error={rebuild.error ? errorMessage(rebuild.error) : undefined}
        onConfirm={runRebuild}
      />
    </div>
  )
}
