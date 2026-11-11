import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { Plus, Users } from 'lucide-react'
import { DataList, type Column } from '@/components/data/data-list'
import { Pagination } from '@/components/data/pagination'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Field } from '@/components/ui/field'
import { Filters } from '@/components/ui/filters'
import { Notice } from '@/components/ui/notice'
import { SecretField } from '@/components/ui/secret-field'
import { useSecretValue } from '@/components/ui/secret-value'
import { PageHeader } from '@/components/ui/page-header'
import { platformCrumb } from '@/features/platform/crumbs'
import { useCreateUser, usePlatformTenants, usePlatformUsers, useUpdateUser } from '@/features/platform/hooks'
import { ListEmpty } from '@/features/platform/list-empty'
import { FormDialog } from '@/components/ui/form-dialog'
import { SearchForm } from '@/features/platform/search-form'
import { Select, TextInput } from '@/components/ui/text-input'
import type { AccountRole, PlatformUser } from '@/features/platform/types'
import { LIST_PAGE_SIZE, useListParams } from '@/features/platform/use-list-params'
import { ApiError } from '@/lib/api/client'
import { QueryState } from '@/components/data/query-state'
import { platformOnlyMessage } from '@/lib/api/failure-messages'

function UserDialog({ user, onClose, onSaved }: { user?: PlatformUser; onClose: () => void; onSaved: (name: string) => void }) {
  useLanguage()
  const [username, setUsername] = useState(user?.username ?? '')
  const [email, setEmail] = useState(user?.email ?? '')
  const [displayName, setDisplayName] = useState(user?.displayName ?? '')
  const [role, setRole] = useState<AccountRole>(user?.role ?? 'UserAdmin')
  const [tenantIds, setTenantIds] = useState<string[]>([])
  const [tenantPage, setTenantPage] = useState(1)
  const password = useSecretValue()
  const create = useCreateUser()
  const update = useUpdateUser(user?.userId ?? '')
  const mutation = user ? update : create
  const invalidField = mutation.error instanceof ApiError ? mutation.error.field : undefined
  function submit() {
    const input = { username: username.trim(), email: email.trim(), displayName: displayName.trim(), role }
    const options = { onSuccess: () => { onSaved(input.displayName); onClose() } }
    if (user) update.mutate(input, options)
    else create.mutate({ ...input, password: password.take(), tenantIds: role === 'UserAdmin' ? tenantIds : [] }, options)
  }
  return <FormDialog title={user ? tx('Kullanıcı düzenle') : tx('Yeni kullanıcı')} submitLabel={user ? tx('Kaydet') : tx('Kullanıcı oluştur')} pendingLabel={tx('Kaydediliyor…')} pending={mutation.isPending} onSubmit={submit} onClose={onClose} error={mutation.error} disabled={!user && role === 'UserAdmin' && tenantIds.length === 0}>
    <Field label={tx('Kullanıcı adı')}>{(control) => <TextInput {...control} value={username} required autoComplete="off" aria-invalid={invalidField === 'username'} onChange={(event) => setUsername(event.target.value)} />}</Field>
    <Field label={tx('E-posta')}>{(control) => <TextInput {...control} type="email" value={email} required autoComplete="off" aria-invalid={invalidField === 'email'} onChange={(event) => setEmail(event.target.value)} />}</Field>
    <Field label={tx('Görünen ad')}>{(control) => <TextInput {...control} value={displayName} required autoComplete="off" aria-invalid={invalidField === 'displayName'} onChange={(event) => setDisplayName(event.target.value)} />}</Field>
    {!user && <SecretField label={tx('Şifre')} required value={password.value} invalid={invalidField === 'password'} onChange={password.set} />}
    <Field label={tx('Rol')}>{(control) => <Select {...control} value={role} aria-invalid={invalidField === 'role'} onChange={(event) => setRole(event.target.value as AccountRole)}><option value="UserAdmin">{tx('UserAdmin')}</option><option value="PlatformAdmin">{tx('PlatformAdmin')}</option></Select>}</Field>
    {!user && role === 'UserAdmin' && <TenantSelection page={tenantPage} setPage={setTenantPage} selected={tenantIds} setSelected={setTenantIds} invalid={invalidField === 'tenantIds'} />}
  </FormDialog>
}

function TenantSelection({ page, setPage, selected, setSelected, invalid }: { page: number; setPage: (page: number) => void; selected: string[]; setSelected: (ids: string[]) => void; invalid: boolean }) {
  const tenants = usePlatformTenants({ page, pageSize: LIST_PAGE_SIZE })
  return <fieldset className="space-y-2" aria-invalid={invalid}>
    <legend className="text-sm font-medium">{tx('Çalışma alanları (en az birini seçin)')}</legend>
    <QueryState isPending={tenants.isPending} error={tenants.error}>
      {tenants.data?.items.map((tenant) => <label key={tenant.tenantId} className="flex items-center gap-2"><input type="checkbox" checked={selected.includes(tenant.tenantId)} onChange={(event) => setSelected(event.target.checked ? [...selected, tenant.tenantId] : selected.filter((id) => id !== tenant.tenantId))} />{tenant.name}</label>)}
      {tenants.data && tenants.data.totalCount > tenants.data.pageSize && <Pagination label={tx('Çalışma alanı sayfaları')} page={page} pageSize={tenants.data.pageSize} totalCount={tenants.data.totalCount} disabled={tenants.isFetching} onPageChange={setPage} />}
    </QueryState>
  </fieldset>
}

export function PlatformUsersPage() {
  useLanguage()
  const { page, get, update } = useListParams()
  const search = get('search')
  const query = usePlatformUsers({ search, page, pageSize: LIST_PAGE_SIZE })
  const [creating, setCreating] = useState(false)
  const [editing, setEditing] = useState<PlatformUser | null>(null)
  const [saved, setSaved] = useState<{ name: string; created: boolean } | null>(null)
  const data = query.data
  const columns: Column<PlatformUser>[] = [
    { key: 'displayName', card: 'title', header: tx('Ad'), wrap: true, render: (user) => <span className="font-medium">{user.displayName}</span> },
    { key: 'platform', card: 'status', header: tx('Platform rolü'), render: (user) => <Badge tone={user.role === 'PlatformAdmin' ? 'warning' : 'neutral'}>{tx(user.role)}</Badge> },
    { key: 'login', header: tx('Kullanıcı adı'), wrap: true, render: (user) => user.username },
    { key: 'memberships', header: tx('Çalışma alanı üyelikleri'), render: (user) => user.membershipCount },
    { key: 'email', header: tx('E-posta'), wrap: true, render: (user) => user.email },
    { key: 'actions', card: 'actions', header: tx('İşlemler'), render: (user) => <Button size="sm" variant="outline" aria-label={tx('{0} kullanıcısını düzenle', { '0': user.displayName })} onClick={() => { setSaved(null); setEditing(user) }}>{tx('Düzenle')}</Button> },
  ]
  return <div className="space-y-6">
    <PageHeader crumbs={[platformCrumb]} title={tx('Kullanıcılar')} description={tx('Platform Yöneticisi yetkisi çalışma alanı rolü değildir; üyelikler çalışma alanı sayfasından yönetilir.')} actions={<Button onClick={() => { setSaved(null); setCreating(true) }}><Plus aria-hidden="true" />{tx('Yeni kullanıcı')}</Button>} />
    {saved && <Notice tone="success">{saved.created ? tx('“{0}” kullanıcısı oluşturuldu.', { '0': saved.name }) : tx('“{0}” kullanıcısı kaydedildi.', { '0': saved.name })}</Notice>}
    <Filters label={tx('Kullanıcı filtreleri')} onClear={search ? () => update({ search: '' }) : undefined}><SearchForm className="sm:col-span-2" label={tx('Kullanıcı ara')} value={search} onSearch={(value) => update({ search: value })} placeholder={tx('Ad veya kullanıcı adı')} /></Filters>
    <QueryState forbiddenMessage={platformOnlyMessage} isPending={query.isPending} error={query.error}>
      {data && <div className="space-y-4">
        {data.totalCount === 0 && !search ? <ListEmpty icon={Users} message={tx('Henüz kullanıcı yok.')} /> : <DataList caption={tx('Kullanıcılar')} rows={data.items} columns={columns} keyExtractor={(user) => user.userId} emptyMessage={tx('Aramaya uygun kullanıcı bulunamadı.')} />}
        {data.totalCount > 0 && <Pagination label={tx('Kullanıcı sayfaları')} page={page} pageSize={data.pageSize} totalCount={data.totalCount} disabled={query.isFetching} onPageChange={(next) => update({ page: next })} />}
      </div>}
    </QueryState>
    {(creating || editing) && <UserDialog user={editing ?? undefined} onClose={() => { setCreating(false); setEditing(null) }} onSaved={(name) => setSaved({ name, created: creating })} />}
  </div>
}
