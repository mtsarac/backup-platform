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
import { PageHeader } from '@/components/ui/page-header'
import { platformCrumb } from '@/features/platform/crumbs'
import { useCreateUser, usePlatformUsers } from '@/features/platform/hooks'
import { ListEmpty } from '@/features/platform/list-empty'
import { FormDialog } from '@/features/platform/plans/form-dialog'
import { PlatformQueryState } from '@/features/platform/platform-query-state'
import { SearchForm } from '@/features/platform/search-form'
import { inputClass } from '@/components/ui/form-styles'
import type { PlatformUser } from '@/features/platform/types'
import { PLATFORM_PAGE_SIZE, useListParams } from '@/features/platform/use-list-params'
import { ApiError } from '@/lib/api/client'
import { errorMessage } from '@/lib/api/failure-messages'

function CreateUserDialog({ onClose, onCreated }: { onClose: () => void; onCreated: (name: string) => void }) {
  useLanguage()
  const [loginIdentifier, setLoginIdentifier] = useState('')
  const [displayName, setDisplayName] = useState('')
  const [password, setPassword] = useState('')
  const create = useCreateUser()
  const invalidField = create.error instanceof ApiError ? create.error.field : undefined
  function submit() {
    create.mutate({ loginIdentifier: loginIdentifier.trim(), displayName: displayName.trim(), password }, {
      onSuccess: (user) => { setLoginIdentifier(''); setDisplayName(''); onCreated(user.displayName); onClose() },
      onSettled: () => setPassword(''),
    })
  }
  return <FormDialog title={tx("Yeni kullanıcı")} description={tx("Bu işlem yalnızca kullanıcı hesabını oluşturur. Tenant üyeliği ve rolünü tenant detayından ekleyin.")} submitLabel="Kullanıcı oluştur" pendingLabel="Oluşturuluyor…" pending={create.isPending} onSubmit={submit} onClose={onClose} error={create.error ? errorMessage(create.error) : undefined}>
    <Field label={tx("Kullanıcı adı")} hint={tx("3-128 karakter: harf, rakam, nokta, alt çizgi, tire.")}>{(control) => <input {...control} className={inputClass} value={loginIdentifier} required autoComplete="off" aria-invalid={invalidField === 'loginIdentifier'} onChange={(event) => setLoginIdentifier(event.target.value)} />}</Field>
    <Field label={tx("Görünen ad")}>{(control) => <input {...control} className={inputClass} value={displayName} required autoComplete="off" aria-invalid={invalidField === 'displayName'} onChange={(event) => setDisplayName(event.target.value)} />}</Field>
    <Field label={tx("Şifre")} hint={tx("En az 12 karakter.")}>{(control) => <input {...control} className={inputClass} type="password" value={password} required autoComplete="new-password" aria-invalid={invalidField === 'password'} onChange={(event) => setPassword(event.target.value)} />}</Field>
  </FormDialog>
}

export function PlatformUsersPage() {
  useLanguage()
  const { page, get, update } = useListParams()
  const search = get('search')
  const query = usePlatformUsers({ search, page, pageSize: PLATFORM_PAGE_SIZE })
  const [creating, setCreating] = useState(false)
  const [created, setCreated] = useState('')
  const data = query.data
  const columns: Column<PlatformUser>[] = [
    { key: 'displayName', header: tx('Ad'), wrap: true, render: (user) => <span className="font-medium">{user.displayName}</span> },
    { key: 'login', header: tx('Kullanıcı adı'), wrap: true, render: (user) => user.loginIdentifier ?? '—' },
    { key: 'platform', header: tx('Platform rolü'), render: (user) => user.isPlatformAdmin ? <Badge tone="warning">{tx("PlatformAdmin")}</Badge> : <span className="text-text-muted">—</span> },
    { key: 'memberships', header: tx('Tenant üyelikleri'), render: (user) => user.membershipCount },
  ]
  return <div className="space-y-6">
    <PageHeader crumbs={[platformCrumb]} title={tx("Users")} description={tx("PlatformAdmin yetkisi tenant rolü değildir; üyelikler tenant detayından yönetilir.")} actions={<Button onClick={() => { setCreated(''); setCreating(true) }}><Plus aria-hidden="true" />{tx("Yeni kullanıcı")}</Button>} />
    {created && <Notice tone="success">“{created}{tx("” kullanıcısı oluşturuldu.")}</Notice>}
    <Filters label={tx("Kullanıcı filtreleri")} onClear={search ? () => update({ search: '' }) : undefined}><SearchForm className="sm:col-span-2" label={tx("Kullanıcı ara")} value={search} onSearch={(value) => update({ search: value })} placeholder={tx("Ad veya kullanıcı adı")} /></Filters>
    <PlatformQueryState isPending={query.isPending} error={query.error}>
      {data && <div className="space-y-4">
        {data.totalCount === 0 && !search ? <ListEmpty icon={Users} message={tx("Henüz kullanıcı yok.")} action={<Button onClick={() => setCreating(true)}>{tx("Yeni kullanıcı")}</Button>} />
          : <DataList rows={data.items} columns={columns} keyExtractor={(user) => user.userId} emptyMessage={tx("Aramaya uygun kullanıcı bulunamadı.")} />}
        {data.totalCount > 0 && <Pagination label={tx("Kullanıcı sayfaları")} page={page} pageSize={data.pageSize} totalCount={data.totalCount} disabled={query.isFetching} onPageChange={(next) => update({ page: next })} />}
      </div>}
    </PlatformQueryState>
    {creating && <CreateUserDialog onClose={() => setCreating(false)} onCreated={setCreated} />}
  </div>
}
