import { useState } from 'react'
import { SearchablePicker } from '@/components/ui/searchable-picker'
import { ErrorNotice } from '@/components/ui/notice'
import { tx } from '@/lib/i18n'
import { usePlatformTenant, usePlatformTenants } from './hooks'

export function TenantPicker({ value, onChange }: { value: string; onChange: (id: string) => void }) {
  const [search, setSearch] = useState('')
  const tenants = usePlatformTenants({ search, page: 1, pageSize: 100 })
  const selected = usePlatformTenant(value && !tenants.data?.items.some(tenant => tenant.tenantId === value) ? value : '')
  return <div className="space-y-2">
    <SearchablePicker label={tx('Tenant')} placeholder={tx('Tenant seçin')} value={value} onChange={onChange}
      options={(tenants.data?.items ?? []).map(tenant => ({ value: tenant.tenantId, label: tenant.name }))}
      selectedLabel={selected.data?.name} onSearchChange={setSearch} />
    {tenants.error && <ErrorNotice title={tx("Tenant'lar alınamadı")} error={tenants.error} onRetry={() => void tenants.refetch()} />}
  </div>
}
