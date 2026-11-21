import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { DataList, type Column } from '@/components/data/data-list'
import { Button } from '@/components/ui/button'
import { FilterField } from '@/components/ui/filters'
import { PageHeader } from '@/components/ui/page-header'
import { platformCrumb } from '@/features/platform/crumbs'
import { ListEmpty } from '@/features/lists/list-empty'
import { pastLastPageMessage } from '@/features/lists/list-messages'
import { ListPager } from '@/features/lists/list-pager'
import { ListToolbar } from '@/features/lists/list-toolbar'
import { toServerQuery } from '@/features/lists/server-list-query'
import { sortProps, useListState } from '@/features/lists/use-list-state'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { usePlans, useSetPlanEnabled } from '@/features/platform/plans/hooks'
import { CreatePlanDialog, EditPlanDialog } from '@/features/platform/plans/plan-form-dialog'
import { formatGb } from '@/features/platform/plans/storage'
import { TechnicalDetails } from '@/components/ui/technical-details'
import type { Plan } from '@/features/platform/plans/types'
import { formatDateTime } from '@/lib/format'
import { StatusPill } from '@/components/data/status-pill'
import { QueryState } from '@/components/data/query-state'
import { platformOnlyMessage } from '@/lib/api/failure-messages'
import { Select } from '@/components/ui/text-input'

const planListConfig = {
  search: true,
  filters: { enabled: ['true', 'false'] },
  sort: { first: { name: 'asc', enabled: 'asc', storageLimitBytes: 'asc', tenantCount: 'asc', createdAt: 'desc' }, default: { key: 'createdAt', dir: 'desc' } },
} as const

export function PlatformPlansPage() {
  useLanguage()
  const state = useListState(planListConfig)
  const query = usePlans(toServerQuery(state))
  const setEnabled = useSetPlanEnabled()
  const [creating, setCreating] = useState(false)
  const [editing, setEditing] = useState<Plan | null>(null)
  const [toggling, setToggling] = useState<Plan | null>(null)
  const data = query.data
  const columns: Column<Plan>[] = [
    { key: 'name', sortable: true, header: tx('Plan'), wrap: true, render: (plan) => <span className="font-medium">{plan.name}</span> },
    { key: 'enabled', sortable: true, card: 'status', header: tx('Durum'), render: (plan) => <StatusPill level={plan.enabled ? 'safe' : 'neutral'}>{plan.enabled ? tx('Etkin') : tx('Devre dışı')}</StatusPill> },
    { key: 'storageLimitBytes', sortable: true, header: tx('Depolama limiti'), render: (plan) => formatGb(plan.storageLimitBytes) },
    { key: 'tenantCount', sortable: true, header: tx('Çalışma alanı sayısı'), render: (plan) => plan.tenantCount },
    { key: 'createdAt', sortable: true, header: tx('Oluşturulma'), render: (plan) => formatDateTime(plan.createdAt) },
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
      ? <span className="text-sm text-text-muted">{tx("Sistem planı; salt okunur.")}</span>
      : <div className="flex flex-wrap gap-2">
        <Button variant="outline" size="sm" aria-label={tx("{0} planını düzenle", { "0": plan.name })} onClick={() => setEditing(plan)}>{tx("Düzenle")}</Button>
        <Button variant="outline" size="sm" aria-label={tx("{0} planını {1}", { "0": plan.name, "1": plan.enabled ? tx('devre dışı bırak') : tx('etkinleştir') })} onClick={() => { setEnabled.reset(); setToggling(plan) }}>{plan.enabled ? tx('Devre dışı bırak') : tx('Etkinleştir')}</Button>
      </div> },
  ]
  return <div className="space-y-6">
    <PageHeader crumbs={[platformCrumb]} title={tx("Plans")} description={tx("Depolama planlarını yönetin. 1 GB = 1.000.000.000 bayt (ondalık).")} actions={<Button onClick={() => setCreating(true)}>{tx("Yeni plan")}</Button>} />
    <ListToolbar state={state} label={tx('Plan filtreleri')} total={data?.totalCount} search={{ label: tx('Plan ara'), placeholder: tx('Plan adı') }}>
      <FilterField label={tx("Durum")}><Select className="text-sm" value={state.filters.enabled} onChange={(event) => state.setFilter('enabled', event.target.value)}>
        <option value="">{tx("Tümü")}</option><option value="true">{tx("Etkin")}</option><option value="false">{tx("Devre dışı")}</option>
      </Select></FilterField>
    </ListToolbar>
    <QueryState forbiddenMessage={platformOnlyMessage} isPending={query.isPending} error={query.error}>
      {data && <div className="space-y-4">
        {data.totalCount === 0 ? <ListEmpty message={tx('Henüz plan yok.')} filtering={state.filtering} filteredMessage={tx('Filtreye uygun plan bulunamadı.')} onClear={state.clear} />
          : <DataList rows={data.items} columns={columns} sort={sortProps(state)} keyExtractor={(plan) => plan.planId} emptyMessage={pastLastPageMessage()} onClearFilters={state.filtering ? state.clear : undefined} />}
        <ListPager state={state} total={data.totalCount} label={tx('Plan sayfaları')} disabled={query.isFetching} />
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
