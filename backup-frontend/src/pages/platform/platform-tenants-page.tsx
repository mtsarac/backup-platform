import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { Building2, Plus } from 'lucide-react'
import { Link } from 'react-router-dom'
import { DataList, type Column } from '@/components/data/data-list'
import { Pagination } from '@/components/data/pagination'
import { Button } from '@/components/ui/button'
import { Field } from '@/components/ui/field'
import { Filters } from '@/components/ui/filters'
import { Notice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { platformCrumb } from '@/features/platform/crumbs'
import { useCreateTenant, usePlatformTenants } from '@/features/platform/hooks'
import { ListEmpty } from '@/features/platform/list-empty'
import { platformPaths } from '@/features/platform/paths'
import { FormDialog } from '@/features/platform/plans/form-dialog'
import { PlatformQueryState } from '@/features/platform/platform-query-state'
import { SearchForm } from '@/features/platform/search-form'
import { inputClass, linkClass } from '@/components/ui/form-styles'
import type { PlatformTenant } from '@/features/platform/types'
import { PLATFORM_PAGE_SIZE, useListParams } from '@/features/platform/use-list-params'
import { ApiError } from '@/lib/api/client'
import { errorMessage } from '@/lib/api/failure-messages'
import { StatusPill } from '@/components/data/status-pill'

function CreateTenantDialog({ onClose, onCreated }: { onClose: () => void; onCreated: (name: string) => void }) {
  useLanguage()
  const [name, setName] = useState('')
  const create = useCreateTenant()
  const nameInvalid = create.error instanceof ApiError && create.error.field === 'name'
  return <FormDialog title={tx("Yeni tenant")} description={tx("Tenant, müşteriye ait yalıtılmış çalışma alanıdır. Üyeleri ve abonelik planını oluşturduktan sonra tenant detayından yönetirsiniz.")}
    submitLabel={tx("Tenant oluştur")} pendingLabel={tx("Oluşturuluyor…")} pending={create.isPending} disabled={!name.trim()}
    error={create.error ? errorMessage(create.error) : undefined} onClose={onClose}
    onSubmit={() => create.mutate(name.trim(), { onSuccess: (tenant) => { onCreated(tenant.name); onClose() } })}>
    <Field label={tx("Tenant adı")}>{(control) => <input {...control} className={inputClass} value={name} maxLength={200} required aria-invalid={nameInvalid} onChange={(event) => setName(event.target.value)} />}</Field>
  </FormDialog>
}

export function PlatformTenantsPage() {
  useLanguage()
  const { page, get, update } = useListParams()
  const search = get('search')
  const query = usePlatformTenants({ search, page, pageSize: PLATFORM_PAGE_SIZE })
  const [creating, setCreating] = useState(false)
  const [created, setCreated] = useState('')
  const data = query.data
  const columns: Column<PlatformTenant>[] = [
    { key: 'name', header: tx('Tenant'), wrap: true, render: (tenant) => <Link className={`${linkClass} font-semibold`} to={platformPaths.tenant(tenant.tenantId)}>{tenant.name}</Link> },
    { key: 'members', header: tx('Üye sayısı'), render: (tenant) => tenant.memberCount },
    { key: 'agents', header: tx('Agents'), render: (tenant) => tenant.agentCount },
    { key: 'enrollment', header: tx('Agent kaydı'), render: (tenant) => <StatusPill level={tenant.enrollmentEnabled ? 'safe' : 'neutral'}>{tenant.enrollmentEnabled ? tx('Açık') : tx('Kapalı')}</StatusPill> },
  ]
  const newTenant = <Button onClick={() => { setCreated(''); setCreating(true) }}><Plus aria-hidden="true" />{tx("Yeni tenant")}</Button>
  return <div className="space-y-6">
    <PageHeader crumbs={[platformCrumb]} title={tx("Tenants")} description={tx("Tüm tenant'ları yönetin; üyelikler ve Agent kayıt politikası tenant detayındadır.")} actions={newTenant} />
    {created && <Notice tone="success">“{created}{tx("” tenant'ı oluşturuldu.")}</Notice>}
    <Filters label={tx("Tenant filtreleri")} onClear={search ? () => update({ search: '' }) : undefined}>
      <SearchForm className="sm:col-span-2" label={tx("Tenant ara")} value={search} onSearch={(value) => update({ search: value })} placeholder={tx("Tenant adı")} />
    </Filters>
    <PlatformQueryState isPending={query.isPending} error={query.error}>
      {data && <div className="space-y-4">
        {data.totalCount === 0 && !search
          ? <ListEmpty icon={Building2} message={tx("Henüz tenant yok.")} action={newTenant} />
          : <DataList rows={data.items} columns={columns} keyExtractor={(tenant) => tenant.tenantId} emptyMessage={tx("Aramaya uygun tenant bulunamadı.")} />}
        {data.totalCount > 0 && <Pagination label={tx("Tenant sayfaları")} page={page} pageSize={data.pageSize} totalCount={data.totalCount} disabled={query.isFetching} onPageChange={(next) => update({ page: next })} />}
      </div>}
    </PlatformQueryState>
    {creating && <CreateTenantDialog onClose={() => setCreating(false)} onCreated={setCreated} />}
  </div>
}
