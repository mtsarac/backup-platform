import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { Link } from 'react-router-dom'
import { RefreshCw, ShieldCheck } from 'lucide-react'
import { DataList, type Column } from '@/components/data/data-list'
import { Pagination } from '@/components/data/pagination'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { buttonVariants } from '@/components/ui/button-variants'
import { FilterField, Filters, filterControlClass } from '@/components/ui/filters'
import { Notice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { Section } from '@/components/ui/section'
import { platformCrumb } from '@/features/platform/crumbs'
import { bareListClass, ListEmpty } from '@/features/platform/list-empty'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { QuotaMeter } from '@/features/storage/quota-meter'
import type { StorageUsageRow, QuotaState, EntitlementStatus } from '@/features/storage/types'
import { platformPaths } from '@/features/platform/paths'
import { PlatformQueryState } from '@/features/platform/platform-query-state'
import { SearchForm } from '@/features/platform/search-form'
import { linkClass } from '@/components/ui/form-styles'
import { PLATFORM_PAGE_SIZE, useListParams } from '@/features/platform/use-list-params'
import { useStorageUsage, useReconcileUsage, useRebuildUsage } from '@/features/platform/storage/hooks'
import { formatBytes, formatDateTime } from '@/lib/format'
import { StatusPill } from '@/components/data/status-pill'
import { TechnicalDetails } from '@/components/ui/technical-details'

export function PlatformStoragePage() {
  useLanguage()
  const { page, get, update } = useListParams()
  const search = get('search')
  const quotaState = (get('quotaState') || undefined) as QuotaState | undefined
  const subscriptionStatus = (get('subscriptionStatus') || undefined) as EntitlementStatus | undefined
  const filtered = Boolean(search || quotaState || subscriptionStatus)

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
            <div className="text-xs text-text-muted">{tx("Henüz hesaplanmadı")}</div>
          )}
          {row.hasUsage && !row.usageCurrent && (
            <Badge tone="warning" className="mt-0.5">{tx("Kullanım hesabı güncel değil")}</Badge>
          )}
        </div>
      ),
    },
    {
      key: 'usage',
      header: tx('Kullanılan / Limit'),
      wrap: true,
      render: (row) => {
        if (!row.hasUsage || row.usedBytes === null) {
          return <span className="text-sm text-text-muted">{tx("Henüz hesaplanmadı")} / {row.limitBytes === null ? row.unlimited ? tx('Limitsiz') : tx('Limit tanımlı değil') : formatBytes(row.limitBytes)}</span>
        }
        const used = row.usedBytes
        const reserved = row.reservedBytes ?? 0
        const limit = row.limitBytes

        return (
          <div className="min-w-0 space-y-1">
            <div className="text-sm tabular-nums">
              <span className="font-semibold text-text-heading">{formatBytes(used + reserved)}</span>
              <span className="text-text-muted"> / {limit === null ? row.unlimited ? tx('Limitsiz') : tx('Limit tanımlı değil') : formatBytes(limit)}</span>
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
          </div>
        )
      },
    },
    {
      key: 'state',
      card: 'status',
      header: tx('Kota Durumu'),
      render: (row) => (
        <div className="space-y-1">
          {row.state ? <StatusPill domain="quota" status={row.state} /> : <StatusPill level="neutral">{tx("Henüz hesaplanmadı")}</StatusPill>}
          {!row.newBackupsAllowed && (
            <div>
              <StatusPill level="urgent">
                {row.denialCode === 'quota_exceeded' ? tx('Kota Doldu') : tx('Engellendi')}
              </StatusPill>
            </div>
          )}
        </div>
      ),
    },
    {
      key: 'technical',
      header: tx('Teknik ayrıntılar'),
      wrap: true,
      render: (row) => <TechnicalDetails items={[
        { label: tx("Çalışma alanı kimliği"), value: row.tenantId },
        { label: tx("Plan"), value: row.plan?.name ?? tx("Abonelik yok") },
        { label: tx("Abonelik"), value: row.subscriptionStatus },
        { label: tx("Politika kimliği"), value: row.policy.policyId },
        { label: tx("Politika anahtarı"), value: row.policy.key },
        { label: tx("Politika sürümü"), value: String(row.policy.version) },
        { label: tx("Hesaplama Modu"), value: row.accountingMode },
        { label: tx("Uygulama Modu"), value: row.enforcementMode },
        { label: tx("Uyarı eşiği (%)"), value: String(row.warningThreshold) },
        { label: tx("Kritik eşik (%)"), value: String(row.criticalThreshold) },
        { label: tx("Kullanılan (bayt)"), value: row.usedBytes === null ? '—' : String(row.usedBytes) },
        { label: tx("Ayrılmış (bayt)"), value: row.reservedBytes === null ? '—' : String(row.reservedBytes) },
        { label: tx("Limit (bayt)"), value: row.limitBytes === null ? '—' : String(row.limitBytes) },
        { label: tx("Kalan (bayt)"), value: row.remainingBytes === null ? '—' : String(row.remainingBytes) },
        { label: tx("Red kodu"), value: row.denialCode ?? '—' },
        { label: tx("Son Hesaplama"), value: formatDateTime(row.calculatedAt, tx('Henüz hesaplanmadı')) },
      ]} />,
    },
    {
      key: 'actions',
      card: 'actions',
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
          >{tx("Kullanımı doğrula")}</Button>
          <Button
            variant="outline"
            size="sm"
            onClick={() => {
              rebuild.reset()
              setActiveRebuild(row)
            }}
          >{tx("Baştan hesapla")}</Button>
        </div>
      ),
    },
  ]

  return (
    <div className="space-y-6">
      <PageHeader crumbs={[platformCrumb]} title={tx("Depolama kullanımı")} description={tx("Hangi çalışma alanı depolama sınırına yaklaşıyor? Kullanılan ve ayrılmış alanı geçerli sınırla karşılaştırın.")} actions={<>
        <Button variant="outline" onClick={() => void query.refetch()} disabled={query.isFetching}><RefreshCw aria-hidden="true" className={`size-4 ${query.isFetching ? 'animate-spin motion-reduce:animate-none' : ''}`} />{tx("Yenile")}</Button>
        <Link to={platformPaths.quotaPolicies} className={buttonVariants({ variant: 'outline' })}><ShieldCheck aria-hidden="true" className="size-4" />{tx("Kota Politikaları")}</Link>
      </>} />
      {statusMessage && <Notice tone="success">{statusMessage}</Notice>}
      <Filters label={tx("Depolama kullanım filtreleri")} onClear={search || quotaState || subscriptionStatus ? () => update({ search: '', quotaState: '', subscriptionStatus: '' }) : undefined}>
        <SearchForm className="sm:col-span-2" label={tx("Tenant ara")} value={search} onSearch={(value) => update({ search: value })} placeholder={tx("Tenant adı...")} />
        <FilterField label={tx("Kota durumu")}><select className={filterControlClass} value={quotaState ?? ''} onChange={(event) => update({ quotaState: event.target.value })}>
          <option value="">{tx("Tümü")}</option><option value="Normal">{tx("Yeterli alan var")}</option><option value="Warning">{tx("Alan azalıyor")}</option><option value="Critical">{tx("Alan dolmak üzere")}</option><option value="OverQuota">{tx("Kota Doldu")}</option><option value="Unlimited">{tx("Limitsiz")}</option>
        </select></FilterField>
        <FilterField label={tx("Abonelik")}><select className={filterControlClass} value={subscriptionStatus ?? ''} onChange={(event) => update({ subscriptionStatus: event.target.value })}>
          <option value="">{tx("Tümü")}</option><option value="Active">{tx("Etkin")}</option><option value="Suspended">{tx("Askıda")}</option><option value="Expired">{tx("Süresi doldu")}</option><option value="NoSubscription">{tx("Abonelik yok")}</option>
        </select></FilterField>
      </Filters>

      <PlatformQueryState isPending={query.isPending} error={query.error}>
        {data && (
          <div className="space-y-4">
            <Section title={tx("Çalışma alanlarının kullanımı")} description={tx("Gösterilen kullanım, kullanılan ve ayrılmış alanın toplamıdır. Doğrulama ve baştan hesaplama yalnızca kullanım kayıtlarını günceller; yedek dosyalarını silmez.")} flush className="overflow-hidden">
              {data.items.length === 0
                ? <ListEmpty message={tx("Kriterlere uygun tenant kullanımı bulunamadı.")} action={filtered
                  ? <Button onClick={() => update({ search: '', quotaState: '', subscriptionStatus: '' })}>{tx("Filtreleri temizle")}</Button>
                  : <Link className={buttonVariants()} to={platformPaths.tenants}>{tx("Çalışma alanlarını görüntüle")}</Link>} />
                : <DataList caption={tx("Depolama kullanımı")} className={bareListClass} rows={data.items} columns={columns} keyExtractor={(row) => row.tenantId} />}
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
        consequence={{
          what: activeReconcile && tx("“{0}” için mevcut yedek dosyaları ve kopyalar taranır, kullanım güncellenir ve süresi geçmiş rezervasyonlar iptal edilir.", { "0": activeReconcile.tenantName }),
          who: tx("Yalnızca bu çalışma alanının kota hesabı etkilenir; yedekler değişmez."),
          reversible: tx("Evet. İşlem yalnızca sayıları günceller; istediğiniz zaman yeniden çalıştırabilirsiniz."),
        }}
        confirmLabel={tx("Uzlaştırmayı Çalıştır")}
        pending={reconcile.isPending}
        error={reconcile.error}
        onConfirm={runReconcile}
      />

      <ConfirmDialog
        open={Boolean(activeRebuild)}
        name={activeRebuild?.tenantName ?? ''}
        onClose={() => setActiveRebuild(null)}
        title={tx("Depolama baştan hesaplansın mı?")}
        consequence={{
          what: activeRebuild && tx("“{0}” için kullanım kaydı baştan hesaplanır. Mevcut yedek kayıtları geçerli kota politikasına göre yeniden taranır.", { "0": activeRebuild.tenantName }),
          who: tx("Yalnızca bu çalışma alanının kota hesabı etkilenir; yedekler değişmez."),
          reversible: tx("Evet. Hesabı istediğiniz zaman yeniden çalıştırabilirsiniz."),
        }}
        confirmLabel={tx("Yeniden Hesaplamayı Çalıştır")}
        pending={rebuild.isPending}
        error={rebuild.error}
        onConfirm={runRebuild}
      />
    </div>
  )
}
