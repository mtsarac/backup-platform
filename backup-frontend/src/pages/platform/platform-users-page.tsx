import { useState, type FormEvent } from 'react'
import { DataTable, type Column } from '@/components/data/data-table'
import { Pagination } from '@/components/data/pagination'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { useCreateUser, usePlatformUsers } from '@/features/platform/hooks'
import { PlatformQueryState } from '@/features/platform/platform-query-state'
import { SearchForm } from '@/features/platform/search-form'
import { inputClass } from '@/features/platform/styles'
import type { PlatformUser } from '@/features/platform/types'
import { PLATFORM_PAGE_SIZE, useListParams } from '@/features/platform/use-list-params'
import { ApiError } from '@/lib/api/client'
import { errorMessage } from '@/lib/api/failure-messages'

function CreateUserForm() {
  const [loginIdentifier, setLoginIdentifier] = useState('')
  const [displayName, setDisplayName] = useState('')
  const [password, setPassword] = useState('')
  const [created, setCreated] = useState('')
  const create = useCreateUser()
  const invalidField = create.error instanceof ApiError ? create.error.field : undefined
  function submit(event: FormEvent) {
    event.preventDefault()
    setCreated('')
    create.mutate({ loginIdentifier: loginIdentifier.trim(), displayName: displayName.trim(), password }, {
      onSuccess: (user) => { setLoginIdentifier(''); setDisplayName(''); setCreated(user.displayName) },
      onSettled: () => setPassword(''),
    })
  }
  return <form onSubmit={submit} className="space-y-3 rounded-lg border border-border bg-surface p-4">
    <h2 className="font-medium text-text-heading">Yeni kullanıcı</h2>
    <div className="grid gap-3 md:grid-cols-3">
      <label className="text-sm text-text-muted">Kullanıcı adı<input className={inputClass} value={loginIdentifier} required autoComplete="off" aria-invalid={invalidField === 'loginIdentifier'} onChange={(event) => setLoginIdentifier(event.target.value)} /><span className="mt-1 block text-xs">3-128 karakter: harf, rakam, nokta, alt çizgi, tire.</span></label>
      <label className="text-sm text-text-muted">Görünen ad<input className={inputClass} value={displayName} required autoComplete="off" aria-invalid={invalidField === 'displayName'} onChange={(event) => setDisplayName(event.target.value)} /></label>
      <label className="text-sm text-text-muted">Şifre<input className={inputClass} type="password" value={password} required autoComplete="new-password" aria-invalid={invalidField === 'password'} onChange={(event) => setPassword(event.target.value)} /><span className="mt-1 block text-xs">En az 12 karakter.</span></label>
    </div>
    <div className="flex flex-wrap items-center gap-3"><Button type="submit" disabled={create.isPending}>{create.isPending ? 'Oluşturuluyor…' : 'Kullanıcı oluştur'}</Button>
      {create.error && <p role="alert" className="text-sm text-error">{errorMessage(create.error)}</p>}
      {created && <p role="status" className="text-sm text-success">“{created}” kullanıcısı oluşturuldu.</p>}</div>
  </form>
}

export function PlatformUsersPage() {
  const { page, get, update } = useListParams()
  const search = get('search')
  const query = usePlatformUsers({ search, page, pageSize: PLATFORM_PAGE_SIZE })
  const data = query.data
  const columns: Column<PlatformUser>[] = [
    { key: 'displayName', header: 'Ad', wrap: true, render: (user) => <span className="font-medium">{user.displayName}</span> },
    { key: 'login', header: 'Kullanıcı adı', wrap: true, render: (user) => user.loginIdentifier ?? '—' },
    { key: 'platform', header: 'Platform rolü', render: (user) => user.isPlatformAdmin ? <Badge tone="warning">PlatformAdmin</Badge> : <span className="text-text-muted">—</span> },
    { key: 'memberships', header: 'Tenant üyelikleri', render: (user) => user.membershipCount },
  ]
  return <div className="space-y-4">
    <div><h1 className="text-2xl font-semibold text-text-heading">Users</h1><p className="mt-1 text-sm text-text-muted">PlatformAdmin yetkisi tenant rolü değildir; üyelikler tenant detayından yönetilir.</p></div>
    <CreateUserForm />
    <div className="flex flex-wrap gap-3"><SearchForm label="Kullanıcı ara" value={search} onSearch={(value) => update({ search: value })} placeholder="Ad veya kullanıcı adı" /></div>
    <PlatformQueryState isPending={query.isPending} error={query.error}>
      {data && <>
        <DataTable rows={data.items} columns={columns} keyExtractor={(user) => user.userId} emptyMessage={search ? 'Aramaya uygun kullanıcı bulunamadı.' : 'Henüz kullanıcı yok.'} />
        {data.totalCount > 0 && <Pagination label="Kullanıcı sayfaları" page={page} pageSize={data.pageSize} totalCount={data.totalCount} disabled={query.isFetching} onPageChange={(next) => update({ page: next })} />}
      </>}
    </PlatformQueryState>
  </div>
}
