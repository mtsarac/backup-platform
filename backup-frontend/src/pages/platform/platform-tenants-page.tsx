import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { Building2, Plus } from 'lucide-react'
import { Link } from 'react-router-dom'
import { DataList, type Column } from '@/components/data/data-list'
import { Button } from '@/components/ui/button'
import { Field } from '@/components/ui/field'
import { Notice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { platformCrumb } from '@/features/platform/crumbs'
import { useCreateTenant, usePlatformTenants } from '@/features/platform/hooks'
import { ListEmpty } from '@/features/lists/list-empty'
import { pastLastPageMessage } from '@/features/lists/list-messages'
import { ListPager } from '@/features/lists/list-pager'
import { ListToolbar } from '@/features/lists/list-toolbar'
import { toServerQuery } from '@/features/lists/server-list-query'
import { sortProps, useListState } from '@/features/lists/use-list-state'
import { platformPaths } from '@/features/platform/paths'
import { FormDialog } from '@/components/ui/form-dialog'
import { linkClass } from '@/components/ui/form-styles'
import { TextInput } from '@/components/ui/text-input'
import type { PlatformTenant } from '@/features/platform/types'
import { ApiError } from '@/lib/api/client'
import { StatusPill } from '@/components/data/status-pill'
import { QueryState } from '@/components/data/query-state'
import { platformOnlyMessage } from '@/lib/api/failure-messages'

function CreateTenantDialog({ onClose, onCreated }: { onClose: () => void; onCreated: (name: string) => void }) {
  useLanguage()
  const [name, setName] = useState('')
  const create = useCreateTenant()
  const nameInvalid = create.error instanceof ApiError && create.error.field === 'name'
  return <FormDialog title={tx("Yeni tenant")} description={tx("Tenant, müşteriye ait yalıtılmış çalışma alanıdır. Üyeleri ve abonelik planını oluşturduktan sonra tenant detayından yönetirsiniz.")}
    submitLabel={tx("Tenant oluştur")} pendingLabel={tx("Oluşturuluyor…")} pending={create.isPending} disabled={!name.trim()}
    error={create.error} onClose={onClose}
    onSubmit={() => create.mutate(name.trim(), { onSuccess: (tenant) => { onCreated(tenant.name); onClose() } })}>
    <Field label={tx("Tenant adı")}>{(control) => <TextInput {...control}  value={name} maxLength={200} required aria-invalid={nameInvalid} onChange={(event) => setName(event.target.value)} />}</Field>
  </FormDialog>
}

const tenantListConfig = {
  search: true,
  sort: { first: { name: 'asc', memberCount: 'asc', agentCount: 'asc', enrollmentEnabled: 'asc' }, default: { key: 'name', dir: 'asc' } },
} as const

export function PlatformTenantsPage() {
  useLanguage()
  const state = useListState(tenantListConfig)
  const query = usePlatformTenants(toServerQuery(state))
  const [creating, setCreating] = useState(false)
  const [created, setCreated] = useState('')
  const data = query.data
  const columns: Column<PlatformTenant>[] = [
    { key: 'name', sortable: true, card: 'title', header: tx('Çalışma alanı'), wrap: true, render: (tenant) => <Link className={`${linkClass} inline-flex min-h-6 items-center font-semibold`} to={platformPaths.tenant(tenant.tenantId)}>{tenant.name}</Link> },
    { key: 'memberCount', sortable: true, header: tx('Kişiler'), render: (tenant) => tenant.memberCount },
    { key: 'agentCount', sortable: true, header: tx('Yedekleme bilgisayarları'), render: (tenant) => tenant.agentCount },
    { key: 'enrollmentEnabled', sortable: true, card: 'status', header: tx('Bilgisayar kaydı'), render: (tenant) => <StatusPill level={tenant.enrollmentEnabled ? 'safe' : 'neutral'}>{tenant.enrollmentEnabled ? tx('Kayıt açık') : tx('Kayıt kapalı')}</StatusPill> },
    { key: 'actions', card: 'actions', header: tx('İşlemler'), render: (tenant) => <Link className={`${linkClass} inline-flex min-h-6 items-center font-medium`} to={platformPaths.tenant(tenant.tenantId)}>{tx('Yönet')}</Link> },
  ]
  const newTenant = <Button onClick={() => { setCreated(''); setCreating(true) }}><Plus aria-hidden="true" />{tx("Yeni tenant")}</Button>
  return <div className="space-y-6">
    <PageHeader crumbs={[platformCrumb]} title={tx("Çalışma alanları")} description={tx("Çalışma alanlarının kişilerini, bilgisayarlarını ve kayıt durumunu inceleyin.")} actions={newTenant} />
    {created && <Notice tone="success">{tx("“{0}” çalışma alanı oluşturuldu.", { "0": created })}</Notice>}
    <ListToolbar state={state} label={tx('Tenant filtreleri')} total={data?.totalCount} search={{ label: tx('Tenant ara'), placeholder: tx('Tenant adı') }} />
    <QueryState forbiddenMessage={platformOnlyMessage} isPending={query.isPending} error={query.error}>
      {data && <div className="space-y-4">
        {data.totalCount === 0
          ? <ListEmpty icon={Building2} message={tx('Henüz tenant yok.')} filtering={state.filtering} filteredMessage={tx('Aramaya uygun tenant bulunamadı.')} onClear={state.clear} />
          : <DataList caption={tx('Çalışma alanları')} rows={data.items} columns={columns} sort={sortProps(state)} keyExtractor={(tenant) => tenant.tenantId} emptyMessage={pastLastPageMessage()} onClearFilters={state.filtering ? state.clear : undefined} />}
        <ListPager state={state} total={data.totalCount} label={tx('Tenant sayfaları')} disabled={query.isFetching} />
      </div>}
    </QueryState>
    {creating && <CreateTenantDialog onClose={() => setCreating(false)} onCreated={setCreated} />}
  </div>
}
