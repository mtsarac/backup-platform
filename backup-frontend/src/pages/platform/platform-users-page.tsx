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
import { useCreateUser, usePlatformUsers } from '@/features/platform/hooks'
import { ListEmpty } from '@/features/platform/list-empty'
import { FormDialog } from '@/components/ui/form-dialog'
import { PlatformQueryState } from '@/features/platform/platform-query-state'
import { SearchForm } from '@/features/platform/search-form'
import { inputClass } from '@/components/ui/form-styles'
import type { PlatformUser } from '@/features/platform/types'
import { PLATFORM_PAGE_SIZE, useListParams } from '@/features/platform/use-list-params'
import { ApiError } from '@/lib/api/client'

function CreateUserDialog({ onClose, onCreated }: { onClose: () => void; onCreated: (name: string) => void }) {
  useLanguage()
  const [loginIdentifier, setLoginIdentifier] = useState('')
  const [displayName, setDisplayName] = useState('')
  const password = useSecretValue()
  const create = useCreateUser()
  const invalidField = create.error instanceof ApiError ? create.error.field : undefined
  function submit() {
    create.mutate({ loginIdentifier: loginIdentifier.trim(), displayName: displayName.trim(), password: password.take() }, {
      onSuccess: (user) => { setLoginIdentifier(''); setDisplayName(''); onCreated(user.displayName); onClose() },
    })
  }
  return <FormDialog title={tx("Yeni kullanıcı")} description={tx("Bu işlem yalnızca kullanıcı hesabını oluşturur. Çalışma alanı üyeliğini ve rolünü çalışma alanı sayfasından ekleyin.")} submitLabel={tx("Kullanıcı oluştur")} pendingLabel={tx("Oluşturuluyor…")} pending={create.isPending} onSubmit={submit} onClose={onClose} error={create.error}>
    <Field label={tx("Kullanıcı adı")} hint={tx("3-128 karakter: harf, rakam, nokta, alt çizgi, tire.")}>{(control) => <input {...control} className={inputClass} value={loginIdentifier} required autoComplete="off" aria-invalid={invalidField === 'loginIdentifier'} onChange={(event) => setLoginIdentifier(event.target.value)} />}</Field>
    <Field label={tx("Görünen ad")}>{(control) => <input {...control} className={inputClass} value={displayName} required autoComplete="off" aria-invalid={invalidField === 'displayName'} onChange={(event) => setDisplayName(event.target.value)} />}</Field>
    <SecretField label={tx("Şifre")} hint={tx("En az 12 karakter.")} required value={password.value} invalid={invalidField === 'password'} onChange={password.set} />
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
    { key: 'displayName', card: 'title', header: tx('Ad'), wrap: true, render: (user) => <span className="font-medium">{user.displayName}</span> },
    { key: 'platform', card: 'status', header: tx('Platform rolü'), render: (user) => user.isPlatformAdmin ? <Badge tone="warning">{tx("PlatformAdmin")}</Badge> : null },
    { key: 'login', header: tx('Kullanıcı adı'), wrap: true, render: (user) => user.loginIdentifier ?? '—' },
    { key: 'memberships', header: tx('Çalışma alanı üyelikleri'), render: (user) => user.membershipCount },
  ]
  return <div className="space-y-6">
    <PageHeader crumbs={[platformCrumb]} title={tx("Kullanıcılar")} description={tx("Platform Yöneticisi yetkisi çalışma alanı rolü değildir; üyelikler çalışma alanı sayfasından yönetilir.")} actions={<Button onClick={() => { setCreated(''); setCreating(true) }}><Plus aria-hidden="true" />{tx("Yeni kullanıcı")}</Button>} />
    {created && <Notice tone="success">{tx("“{0}” kullanıcısı oluşturuldu.", { "0": created })}</Notice>}
    <Filters label={tx("Kullanıcı filtreleri")} onClear={search ? () => update({ search: '' }) : undefined}><SearchForm className="sm:col-span-2" label={tx("Kullanıcı ara")} value={search} onSearch={(value) => update({ search: value })} placeholder={tx("Ad veya kullanıcı adı")} /></Filters>
    <PlatformQueryState isPending={query.isPending} error={query.error}>
      {data && <div className="space-y-4">
        {data.totalCount === 0 && !search ? <ListEmpty icon={Users} message={tx("Henüz kullanıcı yok.")} action={<Button onClick={() => setCreating(true)}>{tx("Yeni kullanıcı")}</Button>} />
          : <DataList caption={tx("Kullanıcılar")} rows={data.items} columns={columns} keyExtractor={(user) => user.userId} emptyMessage={tx("Aramaya uygun kullanıcı bulunamadı.")} />}
        {data.totalCount > 0 && <Pagination label={tx("Kullanıcı sayfaları")} page={page} pageSize={data.pageSize} totalCount={data.totalCount} disabled={query.isFetching} onPageChange={(next) => update({ page: next })} />}
      </div>}
    </PlatformQueryState>
    {creating && <CreateUserDialog onClose={() => setCreating(false)} onCreated={setCreated} />}
  </div>
}
