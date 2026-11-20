import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { Plus, Users } from 'lucide-react'
import { DataList, type Column } from '@/components/data/data-list'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Field } from '@/components/ui/field'
import { Disclosure } from '@/components/ui/disclosure'
import { Notice } from '@/components/ui/notice'
import { SecretField } from '@/components/ui/secret-field'
import { useSecretValue } from '@/components/ui/secret-value'
import { PageHeader } from '@/components/ui/page-header'
import { platformCrumb } from '@/features/platform/crumbs'
import { useAssignTemporaryPassword, useCreateUser, useDisableUser, usePlatformTenants, usePlatformUsers, useUpdateUser } from '@/features/platform/hooks'
import { enableUser } from '@/features/platform/api'
import { ListEmpty } from '@/features/lists/list-empty'
import { pastLastPageMessage } from '@/features/lists/list-messages'
import { ListPager } from '@/features/lists/list-pager'
import { ListToolbar } from '@/features/lists/list-toolbar'
import { toServerQuery } from '@/features/lists/server-list-query'
import { sortProps, useListState, type FilterSpec, type ListState } from '@/features/lists/use-list-state'
import { FormDialog } from '@/components/ui/form-dialog'
import { Select, TextInput } from '@/components/ui/text-input'
import type { AccountRole, PlatformUser } from '@/features/platform/types'
import { ApiError } from '@/lib/api/client'
import { QueryState } from '@/components/data/query-state'
import { platformOnlyMessage } from '@/lib/api/failure-messages'

// The workspaces a new user can be added to, paged inside the dialog; the page is reset when the dialog closes.
const tenantPickerConfig = { pageKey: 'tenants-page', sort: { first: { name: 'asc' }, default: { key: 'name', dir: 'asc' } } } as const

function UserDialog({ user, onClose, onSaved }: { user?: PlatformUser; onClose: () => void; onSaved: (name: string) => void }) {
  useLanguage()
  const [username, setUsername] = useState(user?.username ?? '')
  const [email, setEmail] = useState(user?.email ?? '')
  const [displayName, setDisplayName] = useState(user?.displayName ?? '')
  const [role, setRole] = useState<AccountRole>(user?.role ?? 'UserAdmin')
  const [tenantIds, setTenantIds] = useState<string[]>([])
  const tenantList = useListState(tenantPickerConfig)
  const password = useSecretValue()
  const create = useCreateUser()
  const update = useUpdateUser(user?.userId ?? '')
  const mutation = user ? update : create
  const invalidField = mutation.error instanceof ApiError ? mutation.error.field : undefined
  function close() {
    tenantList.setPage(1)
    onClose()
  }
  function submit() {
    const input = { username: username.trim(), email: email.trim(), displayName: displayName.trim(), role }
    const options = { onSuccess: () => { onSaved(input.displayName); close() } }
    if (user) update.mutate(input, options)
    else create.mutate({ ...input, password: password.take(), tenantIds: role === 'UserAdmin' ? tenantIds : [] }, options)
  }
  return <FormDialog title={user ? tx('Kullanıcı düzenle') : tx('Yeni kullanıcı')} submitLabel={user ? tx('Kaydet') : tx('Kullanıcı oluştur')} pendingLabel={tx('Kaydediliyor…')} pending={mutation.isPending} onSubmit={submit} onClose={close} error={mutation.error} disabled={!user && role === 'UserAdmin' && tenantIds.length === 0}>
    <Field label={tx('Kullanıcı adı')}>{(control) => <TextInput {...control} value={username} required autoComplete="off" aria-invalid={invalidField === 'username'} onChange={(event) => setUsername(event.target.value)} />}</Field>
    <Field label={tx('E-posta')}>{(control) => <TextInput {...control} type="email" value={email} required autoComplete="off" aria-invalid={invalidField === 'email'} onChange={(event) => setEmail(event.target.value)} />}</Field>
    <Field label={tx('Görünen ad')}>{(control) => <TextInput {...control} value={displayName} required autoComplete="off" aria-invalid={invalidField === 'displayName'} onChange={(event) => setDisplayName(event.target.value)} />}</Field>
    {!user && <SecretField label={tx('Şifre')} required value={password.value} invalid={invalidField === 'password'} onChange={password.set} />}
    <Field label={tx('Rol')}>{(control) => <Select {...control} value={role} aria-invalid={invalidField === 'role'} onChange={(event) => setRole(event.target.value as AccountRole)}><option value="UserAdmin">{tx('UserAdmin')}</option><option value="PlatformAdmin">{tx('PlatformAdmin')}</option></Select>}</Field>
    {!user && role === 'UserAdmin' && <TenantSelection state={tenantList} selected={tenantIds} setSelected={setTenantIds} invalid={invalidField === 'tenantIds'} />}
  </FormDialog>
}

function TemporaryPasswordDialog({ user, onClose, onAssigned }: { user: PlatformUser; onClose: () => void; onAssigned: (name: string) => void }) {
  useLanguage()
  const tempPassword = useSecretValue()
  const mutation = useAssignTemporaryPassword(user.userId)
  const invalidField = mutation.error instanceof ApiError ? mutation.error.field : undefined
  function submit() {
    mutation.mutate(tempPassword.take(), { onSuccess: () => { onAssigned(user.displayName); onClose() } })
  }
  return <FormDialog title={tx('Geçici şifre ata')} submitLabel={tx('Geçici şifre ata')} pendingLabel={tx('Atanıyor…')} pending={mutation.isPending} onSubmit={submit} onClose={onClose} error={mutation.error}>
    <p className="text-sm text-text-muted">{tx('Geçici şifre atandığında kullanıcının tüm açık oturumları sonlandırılır ve sonraki girişinde şifresini değiştirmesi zorunlu tutulur.')}</p>
    <SecretField label={tx('Geçici şifre')} hint={tx('En az 12 karakter olmalıdır.')} required value={tempPassword.value} invalid={invalidField === 'temporaryPassword'} onChange={tempPassword.set} />
  </FormDialog>
}

function TenantSelection({ state, selected, setSelected, invalid }: { state: ListState<'name', Record<never, FilterSpec>>; selected: string[]; setSelected: (ids: string[]) => void; invalid: boolean }) {
  const tenants = usePlatformTenants(toServerQuery(state))
  return <fieldset className="space-y-2" aria-invalid={invalid}>
    <legend className="text-sm font-medium">{tx('Çalışma alanları (en az birini seçin)')}</legend>
    <QueryState isPending={tenants.isPending} error={tenants.error}>
      {tenants.data?.items.map((tenant) => <label key={tenant.tenantId} className="flex items-center gap-2"><input type="checkbox" checked={selected.includes(tenant.tenantId)} onChange={(event) => setSelected(event.target.checked ? [...selected, tenant.tenantId] : selected.filter((id) => id !== tenant.tenantId))} />{tenant.name}</label>)}
      {tenants.data && <ListPager state={state} total={tenants.data.totalCount} disabled={tenants.isFetching} label={tx('Çalışma alanı sayfaları')} />}
    </QueryState>
  </fieldset>
}

const userListConfig = {
  search: true,
  sort: {
    first: { displayName: 'asc', username: 'asc', email: 'asc', role: 'asc', membershipCount: 'asc', isDisabled: 'asc' },
    default: { key: 'displayName', dir: 'asc' },
  },
} as const

export function PlatformUsersPage() {
  useLanguage()
  const state = useListState(userListConfig)
  const query = usePlatformUsers(toServerQuery(state))
  const [creating, setCreating] = useState(false)
  const [editing, setEditing] = useState<PlatformUser | null>(null)
  const [disablingUser, setDisablingUser] = useState<PlatformUser | null>(null)
  const [menuEpoch, setMenuEpoch] = useState(0)
  const [assigningTemp, setAssigningTemp] = useState<PlatformUser | null>(null)
  const [disableReason, setDisableReason] = useState('')
  const [actionError, setActionError] = useState<unknown>(null)
  const [saved, setSaved] = useState<{ name: string; created: boolean; action?: string } | null>(null)
  const disableMutation = useDisableUser(disablingUser?.userId ?? '')
  const data = query.data
  const columns: Column<PlatformUser>[] = [
    { key: 'displayName', sortable: true, card: 'title', header: tx('Ad'), wrap: true, render: (user) => <span className="font-medium">{user.displayName}</span> },
    { key: 'role', sortable: true, header: tx('Platform rolü'), render: (user) => <Badge tone={user.role === 'PlatformAdmin' ? 'warning' : 'neutral'}>{tx(user.role)}</Badge> },
    { key: 'isDisabled', sortable: true, card: 'status', header: tx('Durum'), render: (user) => <Badge tone={user.isDisabled ? 'error' : 'neutral'}>{user.isDisabled ? tx('Devre dışı') : tx('Etkin')}</Badge> },
    { key: 'username', sortable: true, header: tx('Kullanıcı adı'), wrap: true, render: (user) => user.username },
    { key: 'membershipCount', sortable: true, header: tx('Çalışma alanı üyelikleri'), render: (user) => user.membershipCount },
    { key: 'email', sortable: true, header: tx('E-posta'), wrap: true, render: (user) => user.email },
    { key: 'actions', card: 'actions', header: tx('İşlemler'), render: (user) => <div className="min-w-28 space-y-1">
      <Button size="sm" variant="outline" aria-label={tx('{0} kullanıcısını düzenle', { '0': user.displayName })} onClick={() => { setSaved(null); setEditing(user) }}>{tx('Düzenle')}</Button>
      <Disclosure key={menuEpoch} title={<>{tx('İşlemler')} <span className="sr-only">{user.displayName} ({user.username})</span></>}>
      <div className="flex flex-wrap gap-2">
      <Button size="sm" variant="outline" aria-label={tx('{0} kullanıcısına geçici şifre ata', { '0': user.displayName })} onClick={() => { setSaved(null); setAssigningTemp(user) }}>{tx('Geçici şifre ata')}</Button>
      {user.isManuallyDisabled ? (
        <Button size="sm" variant="outline" aria-label={tx('{0} kullanıcısını etkinleştir', { '0': user.displayName })} onClick={async () => {
          setSaved(null); setActionError(null);
          try {
            await enableUser(user.userId)
            setSaved({ name: user.displayName, created: false, action: 'enabled' })
            void query.refetch()
          } catch (e) { setActionError(e) }
        }}>{tx('Etkinleştir')}</Button>
      ) : (
        <Button size="sm" variant="outline" aria-label={tx('{0} kullanıcısını devre dışı bırak', { '0': user.displayName })} onClick={() => { setSaved(null); setActionError(null); setDisableReason(''); setDisablingUser(user) }}>{tx('Devre dışı bırak')}</Button>
      )}
      </div>
      </Disclosure>
    </div> },
  ]
  return <div className="space-y-6">
    <PageHeader crumbs={[platformCrumb]} title={tx('Kullanıcılar')} description={tx('Platform Yöneticisi yetkisi çalışma alanı rolü değildir; üyelikler çalışma alanı sayfasından yönetilir.')} actions={<Button onClick={() => { setSaved(null); setCreating(true) }}><Plus aria-hidden="true" />{tx('Yeni kullanıcı')}</Button>} />
    {saved && <Notice tone="success">{saved.action === 'temp' ? tx('“{0}” kullanıcısına geçici şifre atandı.', { '0': saved.name }) : saved.action === 'enabled' ? tx('“{0}” kullanıcısı etkinleştirildi.', { '0': saved.name }) : saved.action === 'disabled' ? tx('“{0}” kullanıcısı devre dışı bırakıldı.', { '0': saved.name }) : saved.created ? tx('“{0}” kullanıcısı oluşturuldu.', { '0': saved.name }) : tx('“{0}” kullanıcısı kaydedildi.', { '0': saved.name })}</Notice>}
    {Boolean(actionError) && <Notice tone="warning">{actionError instanceof ApiError && actionError.code === 'last_platform_admin' ? tx('Son Platform Yöneticisi devre dışı bırakılamaz.') : tx('İşlem başarısız oldu.')}</Notice>}
    <ListToolbar state={state} label={tx('Kullanıcı filtreleri')} total={data?.totalCount} search={{ label: tx('Kullanıcı ara'), placeholder: tx('Ad veya kullanıcı adı') }} />
    <QueryState forbiddenMessage={platformOnlyMessage} isPending={query.isPending} error={query.error}>
      {data && <div className="space-y-4">
        {data.totalCount === 0 ? <ListEmpty icon={Users} message={tx('Henüz kullanıcı yok.')} filtering={state.filtering} filteredMessage={tx('Aramaya uygun kullanıcı bulunamadı.')} onClear={state.clear} /> : <DataList className="[&_td]:!min-w-0" caption={tx('Kullanıcılar')} rows={data.items} columns={columns} sort={sortProps(state)} keyExtractor={(user) => user.userId} emptyMessage={pastLastPageMessage()} onClearFilters={state.filtering ? state.clear : undefined} />}
        <ListPager state={state} total={data.totalCount} label={tx('Kullanıcı sayfaları')} disabled={query.isFetching} />
      </div>}
    </QueryState>
    {(creating || editing) && <UserDialog user={editing ?? undefined} onClose={() => { setCreating(false); setEditing(null) }} onSaved={(name) => setSaved({ name, created: creating })} />}
    {disablingUser && (
      <FormDialog title={tx('Kullanıcıyı devre dışı bırak')} submitLabel={tx('Devre dışı bırak')} pendingLabel={tx('İşleniyor…')} pending={disableMutation.isPending} onSubmit={() => {
        disableMutation.mutate(disableReason.trim() || undefined, {
          onSuccess: () => { setSaved({ name: disablingUser.displayName, created: false, action: 'disabled' }); setDisablingUser(null); void query.refetch() },
          onError: (err) => { setActionError(err); setDisablingUser(null) }
        })
      }} onClose={() => setDisablingUser(null)} error={disableMutation.error}>
        <p className="text-sm text-text-muted">{tx('Hesap devre dışı bırakıldığında tüm aktif oturumları anında sonlandırılır ve giriş yapması engellenir.')}</p>
        <Field label={tx('Devre dışı bırakma nedeni')}>{(control) => <TextInput {...control} value={disableReason} placeholder={tx('İsteğe bağlı açıklama')} onChange={(e) => setDisableReason(e.target.value)} />}</Field>
      </FormDialog>
    )}
    {assigningTemp && <TemporaryPasswordDialog user={assigningTemp} onClose={() => setAssigningTemp(null)} onAssigned={(name) => { setMenuEpoch((epoch) => epoch + 1); setSaved({ name, created: false, action: 'temp' }) }} />}
  </div>
}
