import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { DataTable, type Column } from '@/components/data/data-table'
import { Pagination } from '@/components/data/pagination'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { FilterField, Filters, filterControlClass } from '@/components/ui/filters'
import { PageHeader } from '@/components/ui/page-header'
import { platformCrumb } from '@/features/platform/crumbs'
import { ListEmpty } from '@/features/platform/list-empty'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { PlatformQueryState } from '@/features/platform/platform-query-state'
import { SearchForm } from '@/features/platform/search-form'
import { PLATFORM_PAGE_SIZE, useListParams } from '@/features/platform/use-list-params'
import { usePlans, useSetPlanEnabled } from '@/features/platform/plans/hooks'
import { CreatePlanDialog, EditPlanDialog } from '@/features/platform/plans/plan-form-dialog'
import { StorageLimitText } from '@/features/platform/plans/storage-limit-text'
import type { EnabledFilter, Plan } from '@/features/platform/plans/types'
import { formatDateTime } from '@/lib/format'
import { StatusPill } from '@/components/data/status-pill'

function enabledFilter(value: string): EnabledFilter | undefined {
  return value === 'true' || value === 'false' ? value : undefined
}

export function PlatformPlansPage() {
  useLanguage()
  const { page, get, update } = useListParams()
  const search = get('search')
  const enabled = enabledFilter(get('enabled'))
  const query = usePlans({ enabled, search, page, pageSize: PLATFORM_PAGE_SIZE })
  const setEnabled = useSetPlanEnabled()
  const [creating, setCreating] = useState(false)
  const [editing, setEditing] = useState<Plan | null>(null)
  const [toggling, setToggling] = useState<Plan | null>(null)
  const data = query.data
  const filtered = Boolean(search || enabled)
  const columns: Column<Plan>[] = [
    { key: 'name', header: 'Plan', wrap: true, render: (plan) => <div><span className="font-medium">{plan.name}</span>{plan.quotaPolicyReference && <div className="text-xs text-text-muted">{tx("Politika referansı:")}{' '}{plan.quotaPolicyReference}</div>}</div> },
    { key: 'enabled', header: tx('Durum'), render: (plan) => <StatusPill level={plan.enabled ? 'safe' : 'neutral'}>{plan.enabled ? tx('Etkin') : tx('Devre dışı')}</StatusPill> },
    { key: 'limit', header: tx('Depolama limiti'), render: (plan) => <StorageLimitText bytes={plan.storageLimitBytes} /> },
    { key: 'system', header: tx('Tür'), render: (plan) => plan.isSystem ? <Badge tone="warning">{tx("Sistem / Legacy")}</Badge> : <span className="text-text-muted">{tx("Özel")}</span> },
    { key: 'tenants', header: tx('Tenant sayısı'), render: (plan) => plan.tenantCount },
    { key: 'updated', header: tx('Güncellendi'), render: (plan) => formatDateTime(plan.updatedAt, tx('Henüz bildirilmedi')) },
    { key: 'actions', header: tx('İşlemler'), wrap: true, render: (plan) => plan.isSystem
      ? <span className="text-xs text-text-muted">{tx("Sistem planı; salt okunur.")}</span>
      : <div className="flex flex-wrap gap-2">
        <Button variant="outline" size="sm" aria-label={tx("{0} planını düzenle", { "0": plan.name })} onClick={() => setEditing(plan)}>{tx("Düzenle")}</Button>
        <Button variant="outline" size="sm" aria-label={tx("{0} planını {1}", { "0": plan.name, "1": plan.enabled ? tx('devre dışı bırak') : tx('etkinleştir') })} onClick={() => { setEnabled.reset(); setToggling(plan) }}>{plan.enabled ? tx('Devre dışı bırak') : tx('Etkinleştir')}</Button>
      </div> },
  ]
  return <div className="space-y-6">
    <PageHeader crumbs={[platformCrumb]} title={tx("Plans")} description={tx("Depolama planlarını yönetin. 1 GB = 1.000.000.000 bayt (ondalık).")} actions={<Button onClick={() => setCreating(true)}>{tx("Yeni plan")}</Button>} />
    <Filters label={tx("Plan filtreleri")} onClear={filtered ? () => update({ search: '', enabled: '' }) : undefined}>
      <SearchForm className="sm:col-span-2" label={tx("Plan ara")} value={search} onSearch={(value) => update({ search: value })} placeholder={tx("Plan adı")} />
      <FilterField label={tx("Durum")}><select className={filterControlClass} value={enabled ?? ''} onChange={(event) => update({ enabled: event.target.value })}>
        <option value="">{tx("Tümü")}</option><option value="true">{tx("Etkin")}</option><option value="false">{tx("Devre dışı")}</option>
      </select></FilterField>
    </Filters>
    <PlatformQueryState isPending={query.isPending} error={query.error}>
      {data && <div className="space-y-4">
        {!filtered && data.totalCount === 0 ? <ListEmpty message={tx("Henüz plan yok.")} action={<Button onClick={() => setCreating(true)}>{tx("Yeni plan")}</Button>} />
          : <DataTable rows={data.items} columns={columns} keyExtractor={(plan) => plan.planId} emptyMessage={filtered ? tx('Filtreye uygun plan bulunamadı.') : tx('Henüz plan yok.')} />}
        {data.totalCount > 0 && <Pagination label={tx("Plan sayfaları")} page={page} pageSize={data.pageSize} totalCount={data.totalCount} disabled={query.isFetching} onPageChange={(next) => update({ page: next })} />}
      </div>}
    </PlatformQueryState>
    {creating && <CreatePlanDialog onClose={() => setCreating(false)} />}
    {editing && <EditPlanDialog plan={editing} onClose={() => setEditing(null)} />}
    <ConfirmDialog open={Boolean(toggling)} onClose={() => setToggling(null)} pending={setEnabled.isPending}
      title={toggling?.enabled ? tx('Plan devre dışı bırakılsın mı?') : tx('Plan etkinleştirilsin mi?')}
      consequence={{
        what: toggling && (toggling.enabled ? tx("“{0}” yeni atamalarda seçilemez.", { "0": toggling.name }) : tx("“{0}” yeni atamalarda yeniden seçilebilir olur.", { "0": toggling.name })),
        who: tx("Bu planı kullanan mevcut abonelikler etkilenmez; başka bir plana taşınmaz."),
        reversible: tx("Evet. Planı istediğiniz zaman yeniden etkinleştirebilir veya devre dışı bırakabilirsiniz."),
      }}
      confirmLabel={toggling?.enabled ? tx('Devre dışı bırak') : tx('Etkinleştir')} error={setEnabled.error}
      onConfirm={() => { if (toggling) setEnabled.mutate({ planId: toggling.planId, enabled: !toggling.enabled }, { onSuccess: () => setToggling(null) }) }} />
  </div>
}
