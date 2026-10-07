import { useState, type FormEvent } from 'react'
import { Link } from 'react-router-dom'
import { DataTable, type Column } from '@/components/data/data-table'
import { Pagination } from '@/components/data/pagination'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { useCreateTenant, usePlatformTenants } from '@/features/platform/hooks'
import { platformPaths } from '@/features/platform/paths'
import { PlatformQueryState } from '@/features/platform/platform-query-state'
import { SearchForm } from '@/features/platform/search-form'
import { inputClass, linkClass } from '@/features/platform/styles'
import type { PlatformTenant } from '@/features/platform/types'
import { PLATFORM_PAGE_SIZE, useListParams } from '@/features/platform/use-list-params'
import { ApiError } from '@/lib/api/client'
import { errorMessage } from '@/lib/api/failure-messages'

function CreateTenantForm() {
  const [name, setName] = useState('')
  const [created, setCreated] = useState('')
  const create = useCreateTenant()
  const nameInvalid = create.error instanceof ApiError && create.error.field === 'name'
  function submit(event: FormEvent) {
    event.preventDefault()
    setCreated('')
    create.mutate(name.trim(), { onSuccess: (tenant) => { setName(''); setCreated(tenant.name) } })
  }
  return <form onSubmit={submit} className="space-y-3 rounded-lg border border-border bg-surface p-4">
    <h2 className="font-medium text-text-heading">Yeni tenant</h2>
    <div className="flex flex-wrap items-end gap-3">
      <label className="min-w-0 flex-1 text-sm text-text-muted">Tenant adı<input className={inputClass} value={name} maxLength={200} required aria-invalid={nameInvalid} onChange={(event) => setName(event.target.value)} /></label>
      <Button type="submit" disabled={create.isPending || !name.trim()}>{create.isPending ? 'Oluşturuluyor…' : 'Tenant oluştur'}</Button>
    </div>
    {create.error && <p role="alert" className="text-sm text-error">{errorMessage(create.error)}</p>}
    {created && <p role="status" className="text-sm text-success">“{created}” tenant'ı oluşturuldu.</p>}
  </form>
}

export function PlatformTenantsPage() {
  const { page, get, update } = useListParams()
  const search = get('search')
  const query = usePlatformTenants({ search, page, pageSize: PLATFORM_PAGE_SIZE })
  const data = query.data
  const columns: Column<PlatformTenant>[] = [
    { key: 'name', header: 'Tenant', wrap: true, render: (tenant) => <Link className={linkClass} to={platformPaths.tenant(tenant.tenantId)}>{tenant.name}</Link> },
    { key: 'members', header: 'Üye sayısı', render: (tenant) => tenant.memberCount },
    { key: 'agents', header: 'Agents', render: (tenant) => tenant.agentCount },
    { key: 'enrollment', header: 'Agent kaydı', render: (tenant) => <Badge tone={tenant.enrollmentEnabled ? 'success' : 'neutral'}>{tenant.enrollmentEnabled ? 'Açık' : 'Kapalı'}</Badge> },
  ]
  return <div className="space-y-4">
    <div><h1 className="text-2xl font-semibold text-text-heading">Tenants</h1><p className="mt-1 text-sm text-text-muted">Tüm tenant'ları yönetin; üyelikler ve Agent kayıt politikası tenant detayındadır.</p></div>
    <CreateTenantForm />
    <div className="flex flex-wrap gap-3"><SearchForm label="Tenant ara" value={search} onSearch={(value) => update({ search: value })} placeholder="Tenant adı" /></div>
    <PlatformQueryState isPending={query.isPending} error={query.error}>
      {data && <>
        <DataTable rows={data.items} columns={columns} keyExtractor={(tenant) => tenant.tenantId} emptyMessage={search ? 'Aramaya uygun tenant bulunamadı.' : 'Henüz tenant yok.'} />
        {data.totalCount > 0 && <Pagination label="Tenant sayfaları" page={page} pageSize={data.pageSize} totalCount={data.totalCount} disabled={query.isFetching} onPageChange={(next) => update({ page: next })} />}
      </>}
    </PlatformQueryState>
  </div>
}
