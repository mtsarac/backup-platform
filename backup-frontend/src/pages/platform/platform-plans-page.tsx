import { useState } from 'react'
import { DataTable, type Column } from '@/components/data/data-table'
import { Pagination } from '@/components/data/pagination'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { agentDate } from '@/features/agents/format'
import { ConfirmDialog } from '@/features/agents/confirm-dialog'
import { PlatformQueryState } from '@/features/platform/platform-query-state'
import { SearchForm } from '@/features/platform/search-form'
import { selectClass } from '@/features/platform/styles'
import { PLATFORM_PAGE_SIZE, useListParams } from '@/features/platform/use-list-params'
import { usePlans, useSetPlanEnabled } from '@/features/platform/plans/hooks'
import { CreatePlanDialog, EditPlanDialog } from '@/features/platform/plans/plan-form-dialog'
import { StorageLimitText } from '@/features/platform/plans/storage-limit-text'
import type { EnabledFilter, Plan } from '@/features/platform/plans/types'
import { errorMessage } from '@/lib/api/failure-messages'

function enabledFilter(value: string): EnabledFilter | undefined {
  return value === 'true' || value === 'false' ? value : undefined
}

export function PlatformPlansPage() {
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
    { key: 'name', header: 'Plan', wrap: true, render: (plan) => <div><span className="font-medium">{plan.name}</span>{plan.quotaPolicyReference && <div className="text-xs text-text-muted">Politika referansı: {plan.quotaPolicyReference}</div>}</div> },
    { key: 'enabled', header: 'Durum', render: (plan) => <Badge tone={plan.enabled ? 'success' : 'neutral'}>{plan.enabled ? 'Etkin' : 'Devre dışı'}</Badge> },
    { key: 'limit', header: 'Depolama limiti', render: (plan) => <StorageLimitText bytes={plan.storageLimitBytes} /> },
    { key: 'system', header: 'Tür', render: (plan) => plan.isSystem ? <Badge tone="warning">Sistem / Legacy</Badge> : <span className="text-text-muted">Özel</span> },
    { key: 'tenants', header: 'Tenant sayısı', render: (plan) => plan.tenantCount },
    { key: 'updated', header: 'Güncellendi', render: (plan) => agentDate(plan.updatedAt) },
    { key: 'actions', header: 'İşlemler', wrap: true, render: (plan) => plan.isSystem
      ? <span className="text-xs text-text-muted">Sistem planı; salt okunur.</span>
      : <div className="flex flex-wrap gap-2">
        <Button variant="outline" size="sm" aria-label={`${plan.name} planını düzenle`} onClick={() => setEditing(plan)}>Düzenle</Button>
        <Button variant="outline" size="sm" aria-label={`${plan.name} planını ${plan.enabled ? 'devre dışı bırak' : 'etkinleştir'}`} onClick={() => { setEnabled.reset(); setToggling(plan) }}>{plan.enabled ? 'Devre dışı bırak' : 'Etkinleştir'}</Button>
      </div> },
  ]
  return <div className="space-y-4">
    <div className="flex flex-wrap items-start justify-between gap-3">
      <div><h1 className="text-2xl font-semibold text-text-heading">Plans</h1><p className="mt-1 text-sm text-text-muted">Depolama planlarını yönetin. 1 GB = 1.000.000.000 bayt (ondalık).</p></div>
      <Button onClick={() => setCreating(true)}>Yeni plan</Button>
    </div>
    <div className="flex flex-wrap items-end gap-3">
      <SearchForm label="Plan ara" value={search} onSearch={(value) => update({ search: value })} placeholder="Plan adı" />
      <label className="text-sm text-text-muted">Durum<select className={selectClass} value={enabled ?? ''} onChange={(event) => update({ enabled: event.target.value })}>
        <option value="">Tümü</option><option value="true">Etkin</option><option value="false">Devre dışı</option>
      </select></label>
    </div>
    <PlatformQueryState isPending={query.isPending} error={query.error}>
      {data && <>
        <DataTable rows={data.items} columns={columns} keyExtractor={(plan) => plan.planId} emptyMessage={filtered ? 'Filtreye uygun plan bulunamadı.' : 'Henüz plan yok.'} />
        {data.totalCount > 0 && <Pagination label="Plan sayfaları" page={page} pageSize={data.pageSize} totalCount={data.totalCount} disabled={query.isFetching} onPageChange={(next) => update({ page: next })} />}
      </>}
    </PlatformQueryState>
    {creating && <CreatePlanDialog onClose={() => setCreating(false)} />}
    {editing && <EditPlanDialog plan={editing} onClose={() => setEditing(null)} />}
    <ConfirmDialog open={Boolean(toggling)} onClose={() => setToggling(null)} pending={setEnabled.isPending}
      title={toggling?.enabled ? 'Plan devre dışı bırakılsın mı?' : 'Plan etkinleştirilsin mi?'}
      description={toggling ? (toggling.enabled ? `“${toggling.name}” yeni atamalarda seçilemez. Bu planı kullanan mevcut abonelikler başka bir plana taşınmaz.` : `“${toggling.name}” yeni atamalarda yeniden seçilebilir olacak.`) : ''}
      confirmLabel={toggling?.enabled ? 'Devre dışı bırak' : 'Etkinleştir'} error={setEnabled.error ? errorMessage(setEnabled.error) : undefined}
      onConfirm={() => { if (toggling) setEnabled.mutate({ planId: toggling.planId, enabled: !toggling.enabled }, { onSuccess: () => setToggling(null) }) }} />
  </div>
}
