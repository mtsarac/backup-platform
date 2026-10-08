import { useState } from 'react'
import { Link } from 'react-router-dom'
import { RefreshCw, ShieldCheck } from 'lucide-react'
import { DataTable, type Column } from '@/components/data/data-table'
import { Pagination } from '@/components/data/pagination'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { ConfirmDialog } from '@/features/agents/confirm-dialog'
import { agentDate } from '@/features/agents/format'
import { formatCapacity, allocatedPercent } from '@/features/storage/format'
import { QuotaStateBadge, EntitlementStatusBadge, PlanName } from '@/features/storage/quota-badges'
import { QuotaMeter } from '@/features/storage/quota-meter'
import type { StorageUsageRow, QuotaState, EntitlementStatus } from '@/features/storage/types'
import { platformPaths } from '@/features/platform/paths'
import { PlatformQueryState } from '@/features/platform/platform-query-state'
import { SearchForm } from '@/features/platform/search-form'
import { linkClass, selectClass } from '@/features/platform/styles'
import { PLATFORM_PAGE_SIZE, useListParams } from '@/features/platform/use-list-params'
import { errorMessage } from '@/lib/api/failure-messages'
import { useStorageUsage, useReconcileUsage, useRebuildUsage } from '@/features/platform/storage/hooks'

export function PlatformStoragePage() {
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
          `“${activeReconcile.tenantName}” için reconcile tamamlandı: ${
            res.drifted ? 'sapma düzeltildi' : 'sapma yok'
          }.`,
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
        setStatusMessage(`“${activeRebuild.tenantName}” için kullanım baştan hesaplandı (Rebuild).`)
      },
    })
  }

  const columns: Column<StorageUsageRow>[] = [
    {
      key: 'tenant',
      header: 'Tenant',
      wrap: true,
      render: (row) => (
        <div>
          <Link className={linkClass} to={platformPaths.tenant(row.tenantId)}>
            {row.tenantName}
          </Link>
          {!row.hasUsage && (
            <div className="text-xs text-text-muted">Projeksiyon yok</div>
          )}
          {row.hasUsage && !row.usageCurrent && (
            <Badge tone="warning" className="mt-0.5 text-[10px]">Eski Politika</Badge>
          )}
        </div>
      ),
    },
    {
      key: 'plan',
      header: 'Plan & Durum',
      render: (row) => (
        <div className="space-y-1">
          <div><PlanName plan={row.plan} /></div>
          <div><EntitlementStatusBadge status={row.subscriptionStatus} /></div>
        </div>
      ),
    },
    {
      key: 'usage',
      header: 'Kullanılan / Limit',
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
          <div className="min-w-[160px] space-y-1">
            <div className="text-xs">
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
                label={`${row.tenantName} doluluk oranı`}
              />
            )}
            {percent !== null && (
              <div className="text-[11px] text-text-muted">%%{percent} ayrılmış</div>
            )}
          </div>
        )
      },
    },
    {
      key: 'state',
      header: 'Kota Durumu',
      render: (row) => (
        <div className="space-y-1">
          {row.state ? <QuotaStateBadge state={row.state} /> : <span className="text-xs text-text-muted">—</span>}
          {!row.newBackupsAllowed && (
            <div>
              <Badge tone="error" className="text-[10px]">
                {row.denialCode === 'quota_exceeded' ? 'Kota Doldu' : 'Engellendi'}
              </Badge>
            </div>
          )}
        </div>
      ),
    },
    {
      key: 'policy',
      header: 'Politika',
      render: (row) => (
        <div className="text-xs">
          <span className="font-medium">{row.policy.key}</span>
          <span className="text-text-muted"> (v{row.policy.version})</span>
        </div>
      ),
    },
    {
      key: 'calculatedAt',
      header: 'Son Hesaplama',
      render: (row) => (
        <span className="text-xs text-text-muted">
          {row.calculatedAt ? agentDate(row.calculatedAt) : '—'}
        </span>
      ),
    },
    {
      key: 'actions',
      header: 'İşlemler',
      render: (row) => (
        <div className="flex flex-wrap gap-1.5">
          <Button
            variant="outline"
            size="sm"
            onClick={() => {
              reconcile.reset()
              setActiveReconcile(row)
            }}
          >
            Reconcile
          </Button>
          <Button
            variant="outline"
            size="sm"
            onClick={() => {
              rebuild.reset()
              setActiveRebuild(row)
            }}
          >
            Rebuild
          </Button>
        </div>
      ),
    },
  ]

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h1 className="text-2xl font-semibold text-text-heading">Depolama ve Kota Yönetimi</h1>
          <p className="mt-1 text-sm text-text-muted">
            Tüm tenant'ların depolama kullanımını, rezerve alanlarını ve kota durumlarını izleyin.
          </p>
        </div>
        <div className="flex items-center gap-2">
          <Button
            variant="outline"
            onClick={() => void query.refetch()}
            disabled={query.isFetching}
          >
            <RefreshCw className={`size-4 ${query.isFetching ? 'animate-spin' : ''}`} />
            Yenile
          </Button>
          <Link
            to={platformPaths.quotaPolicies}
            className="inline-flex h-8 items-center gap-1.5 rounded-lg border border-border bg-background px-2.5 text-sm font-medium hover:bg-muted"
          >
            <ShieldCheck className="size-4" />
            Kota Politikaları
          </Link>
        </div>
      </div>

      {statusMessage && (
        <p role="status" className="rounded-md border border-success/30 bg-success-subtle p-3 text-sm text-success">
          {statusMessage}
        </p>
      )}

      <div className="flex flex-wrap items-end gap-3">
        <SearchForm
          label="Tenant ara"
          value={search}
          onSearch={(val) => update({ search: val })}
          placeholder="Tenant adı..."
        />
        <label className="text-sm text-text-muted">
          Kota durumu
          <select
            className={selectClass}
            value={quotaState ?? ''}
            onChange={(e) => update({ quotaState: e.target.value })}
          >
            <option value="">Tümü</option>
            <option value="Normal">Normal</option>
            <option value="Warning">Warning</option>
            <option value="Critical">Critical</option>
            <option value="OverQuota">OverQuota</option>
            <option value="Unlimited">Unlimited</option>
          </select>
        </label>
        <label className="text-sm text-text-muted">
          Abonelik
          <select
            className={selectClass}
            value={subscriptionStatus ?? ''}
            onChange={(e) => update({ subscriptionStatus: e.target.value })}
          >
            <option value="">Tümü</option>
            <option value="Active">Etkin</option>
            <option value="Suspended">Askıda</option>
            <option value="Expired">Süresi doldu</option>
            <option value="NoSubscription">Abonelik yok</option>
          </select>
        </label>
      </div>

      <PlatformQueryState isPending={query.isPending} error={query.error}>
        {data && (
          <>
            <DataTable
              rows={data.items}
              columns={columns}
              keyExtractor={(r) => r.tenantId}
              emptyMessage="Kriterlere uygun tenant kullanımı bulunamadı."
            />
            {data.totalCount > 0 && (
              <Pagination
                label="Kullanım sayfaları"
                page={page}
                pageSize={data.pageSize}
                totalCount={data.totalCount}
                disabled={query.isFetching}
                onPageChange={(next) => update({ page: next })}
              />
            )}
          </>
        )}
      </PlatformQueryState>

      <ConfirmDialog
        open={Boolean(activeReconcile)}
        onClose={() => setActiveReconcile(null)}
        title="Depolama senkronizasyonu çalıştırılsın mı (Reconcile)?"
        description={
          activeReconcile
            ? `“${activeReconcile.tenantName}” için mevcut artifact ve replikalar taranacak, kullanım güncellenecek ve süresi geçmiş rezervasyonlar iptal edilecektir.`
            : ''
        }
        confirmLabel="Reconcile Çalıştır"
        pending={reconcile.isPending}
        error={reconcile.error ? errorMessage(reconcile.error) : undefined}
        onConfirm={runReconcile}
      />

      <ConfirmDialog
        open={Boolean(activeRebuild)}
        onClose={() => setActiveRebuild(null)}
        title="Depolama baştan hesaplansın mı (Rebuild)?"
        description={
          activeRebuild
            ? `“${activeRebuild.tenantName}” için kullanım kaydı baştan hesaplanacaktır. Mevcut veritabanı kayıtları geçerli kota politikasına göre yeniden taranır.`
            : ''
        }
        confirmLabel="Rebuild Çalıştır"
        pending={rebuild.isPending}
        error={rebuild.error ? errorMessage(rebuild.error) : undefined}
        onConfirm={runRebuild}
      />
    </div>
  )
}
