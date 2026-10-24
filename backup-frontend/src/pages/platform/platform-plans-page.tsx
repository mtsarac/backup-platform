import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { DataList, type Column } from '@/components/data/data-list'
import { Pagination } from '@/components/data/pagination'
import { Button } from '@/components/ui/button'
import { FilterField, Filters, filterControlClass } from '@/components/ui/filters'
import { PageHeader } from '@/components/ui/page-header'
import { platformCrumb } from '@/features/platform/crumbs'
import { ListEmpty } from '@/features/platform/list-empty'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { SearchForm } from '@/features/platform/search-form'
import { PLATFORM_PAGE_SIZE, useListParams } from '@/features/platform/use-list-params'
import { usePlans, useSetPlanEnabled } from '@/features/platform/plans/hooks'
import { CreatePlanDialog, EditPlanDialog } from '@/features/platform/plans/plan-form-dialog'
import { formatGb } from '@/features/platform/plans/storage'
import { TechnicalDetails } from '@/components/ui/technical-details'
import type { EnabledFilter, Plan } from '@/features/platform/plans/types'
import { formatDateTime } from '@/lib/format'
import { StatusPill } from '@/components/data/status-pill'
import { QueryState } from '@/components/data/query-state'
import { platformOnlyMessage } from '@/components/auth/access-denied'

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
    { key: 'name', header: tx('Plan'), wrap: true, render: (plan) => <span className="font-medium">{plan.name}</span> },
    { key: 'enabled', card: 'status', header: tx('Durum'), render: (plan) => <StatusPill level={plan.enabled ? 'safe' : 'neutral'}>{plan.enabled ? tx('Etkin') : tx('Devre dışı')}</StatusPill> },
    { key: 'limit', header: tx('Depolama limiti'), render: (plan) => formatGb(plan.storageLimitBytes) },
    { key: 'tenants', header: tx('Çalışma alanı sayısı'), render: (plan) => plan.tenantCount },
    { key: 'technical', header: tx('Teknik ayrıntılar'), wrap: true, render: (plan) => <TechnicalDetails items={[
      { label: tx("Plan kimliği"), value: plan.planId },
      { label: tx("Limit (bayt)"), value: plan.storageLimitBytes === null ? '—' : String(plan.storageLimitBytes) },
      { label: tx("Politika referansı"), value: plan.quotaPolicyReference ?? '—' },
      { label: tx("Politika sürümü"), value: String(plan.policyConfigurationVersion) },
      { label: tx("Sistem anahtarı"), value: plan.systemKey ?? '—' },
      { label: tx("Oluşturulma zamanı"), value: formatDateTime(plan.createdAt) },
      { label: tx("Güncellendi"), value: formatDateTime(plan.updatedAt) },
    ]} /> },
    { key: 'actions', card: 'actions', header: tx('İşlemler'), wrap: true, render: (plan) => plan.isSystem
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
    <QueryState forbiddenMessage={platformOnlyMessage} isPending={query.isPending} error={query.error}>
      {data && <div className="space-y-4">
        {data.items.length === 0 ? <ListEmpty message={filtered ? tx('Filtreye uygun plan bulunamadı.') : tx("Henüz plan yok.")} action={filtered
          ? <Button onClick={() => update({ search: '', enabled: '' })}>{tx("Filtreleri temizle")}</Button>
          : <Button onClick={() => setCreating(true)}>{tx("Yeni plan")}</Button>} />
          : <DataList rows={data.items} columns={columns} keyExtractor={(plan) => plan.planId} />}
        {data.totalCount > 0 && <Pagination label={tx("Plan sayfaları")} page={page} pageSize={data.pageSize} totalCount={data.totalCount} disabled={query.isFetching} onPageChange={(next) => update({ page: next })} />}
      </div>}
    </QueryState>
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
