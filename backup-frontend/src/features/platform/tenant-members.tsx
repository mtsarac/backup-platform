import { tx, useLanguage } from '@/lib/i18n'
import { useState, type FormEvent } from 'react'
import { Users } from 'lucide-react'
import { DataTable, type Column } from '@/components/data/data-table'
import { Pagination } from '@/components/data/pagination'
import { Button } from '@/components/ui/button'
import { EmptyState } from '@/components/ui/empty-state'
import { Field } from '@/components/ui/field'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { Section } from '@/components/ui/section'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { errorMessage } from '@/lib/api/failure-messages'
import { useAddMembership, useChangeMembershipRole, usePlatformMemberships, usePlatformUsers, useRemoveMembership } from './hooks'
import { bareTableClass } from './list-empty'
import { PlatformQueryState } from './platform-query-state'
import { SearchForm } from './search-form'
import { inlineSelectClass, selectClass } from './styles'
import { membershipRoles, type MembershipRole, type PlatformMembership } from './types'
import { PLATFORM_PAGE_SIZE, useListParams } from './use-list-params'

function isRole(value: string): value is MembershipRole {
  return membershipRoles.some((role) => role === value)
}

function AddMemberForm({ tenantId }: { tenantId: string }) {
  useLanguage()
  const [userSearch, setUserSearch] = useState('')
  const [userId, setUserId] = useState('')
  const [role, setRole] = useState<MembershipRole>('Viewer')
  const [added, setAdded] = useState('')
  const users = usePlatformUsers({ search: userSearch, page: 1, pageSize: 20 })
  const add = useAddMembership(tenantId)
  function submit(event: FormEvent) {
    event.preventDefault()
    setAdded('')
    const chosen = users.data?.items.find((user) => user.userId === userId)
    add.mutate({ userId, role }, { onSuccess: () => { setUserId(''); setAdded(chosen?.displayName ?? '') } })
  }
  return <Section title={tx("Üye ekle")} description={tx("Var olan bir kullanıcıyı bu tenant'a bir rolle ekleyin. Kullanıcı hesabı burada oluşturulmaz.")}>
    <div className="space-y-4">
      <div className="flex max-w-xl"><SearchForm label={tx("Kullanıcı ara")} value={userSearch} onSearch={(value) => { setUserSearch(value); setUserId('') }} placeholder={tx("Ad veya kullanıcı adı")} /></div>
      <form onSubmit={submit} className="grid items-end gap-4 sm:grid-cols-[minmax(0,1fr)_10rem_auto]">
        <Field label={tx("Kullanıcı")}>{(control) => <select {...control} className={`${selectClass} w-full`} value={userId} onChange={(event) => setUserId(event.target.value)} disabled={users.isPending}>
          <option value="">{users.isPending ? tx('Kullanıcılar yükleniyor…') : users.error ? tx('Kullanıcılar alınamadı') : tx('Kullanıcı seçin')}</option>
          {users.data?.items.map((user) => <option key={user.userId} value={user.userId}>{user.displayName}{user.loginIdentifier ? ` (${user.loginIdentifier})` : ''}{user.isPlatformAdmin ? tx(' · PlatformAdmin') : ''}</option>)}
        </select>}</Field>
        <Field label={tx("Rol")}>{(control) => <select {...control} className={`${selectClass} w-full`} value={role} onChange={(event) => { if (isRole(event.target.value)) setRole(event.target.value) }}>{membershipRoles.map((item) => <option key={item} value={item}>{item}</option>)}</select>}</Field>
        <Button type="submit" disabled={!userId || add.isPending}>{add.isPending ? 'Ekleniyor…' : tx('Üye ekle')}</Button>
      </form>
      {users.error && <p role="alert" className="text-sm text-error">{errorMessage(users.error)}</p>}
      {users.data && users.data.totalCount > users.data.items.length && <p className="text-sm text-text-muted">{tx("İlk")}{' '}{users.data.items.length}{' '}{tx("kullanıcı gösteriliyor; listeyi daraltmak için arayın.")}</p>}
      {add.error && <p role="alert" className="text-sm text-error">{errorMessage(add.error)}</p>}
      {added && <Notice tone="success">“{added}{tx("” tenant'a eklendi.")}</Notice>}
    </div>
  </Section>
}

export function MembershipsSection({ tenantId, tenantName }: { tenantId: string; tenantName: string }) {
  useLanguage()
  const { page, update } = useListParams('memberPage')
  const query = usePlatformMemberships(tenantId, { page, pageSize: PLATFORM_PAGE_SIZE })
  const changeRole = useChangeMembershipRole(tenantId)
  const remove = useRemoveMembership(tenantId)
  const [selected, setSelected] = useState<PlatformMembership | null>(null)
  const data = query.data
  const columns: Column<PlatformMembership>[] = [
    { key: 'name', header: tx('Ad'), wrap: true, render: (member) => <span className="font-medium">{member.displayName}</span> },
    { key: 'login', header: tx('Kullanıcı adı'), wrap: true, render: (member) => member.loginIdentifier ?? '—' },
    { key: 'role', header: tx('Rol'), render: (member) => <select aria-label={tx("{0} rolü", { "0": member.displayName })} className={inlineSelectClass} value={member.role} disabled={changeRole.isPending} onChange={(event) => { if (isRole(event.target.value)) changeRole.mutate({ userId: member.userId, role: event.target.value }) }}>{membershipRoles.map((role) => <option key={role} value={role}>{role}</option>)}</select> },
    { key: 'actions', header: tx('İşlemler'), render: (member) => <Button variant="destructive" size="sm" aria-label={tx("{0} üyeliğini kaldır", { "0": member.displayName })} onClick={() => { remove.reset(); setSelected(member) }}>{tx("Kaldır")}</Button> },
  ]
  return <div className="space-y-6">
    <AddMemberForm tenantId={tenantId} />
    <Section title={tx("Üyelikler")} description={tx("Rol değişikliği hemen uygulanır; üyeliği kaldırmak kullanıcı hesabını silmez.")} flush className="overflow-hidden">
      {changeRole.error && <div className="p-4"><ErrorNotice error={changeRole.error} /></div>}
      <PlatformQueryState isPending={query.isPending} error={query.error}>
        {data && (data.totalCount === 0
          ? <EmptyState icon={Users} message={tx("Bu tenant'ın henüz üyesi yok; yukarıdan bir kullanıcı ekleyin.")} />
          : <DataTable className={bareTableClass} rows={data.items} columns={columns} keyExtractor={(member) => member.userId} emptyMessage={tx("Bu tenant'ın henüz üyesi yok.")} />)}
      </PlatformQueryState>
    </Section>
    {data && data.totalCount > 0 && <Pagination label={tx("Üyelik sayfaları")} page={page} pageSize={data.pageSize} totalCount={data.totalCount} disabled={query.isFetching} onPageChange={(next) => update({ memberPage: next })} />}
    <ConfirmDialog open={Boolean(selected)} onClose={() => setSelected(null)} title={tx("Üyelik kaldırılsın mı?")} confirmLabel={tx("Üyeliği kaldır")}
      consequence={{
        what: selected && tx("{0} kullanıcısı “{1}” çalışma alanından çıkarılır.", { "0": selected.displayName, "1": tenantName }),
        who: tx("Yalnızca bu kullanıcının bu çalışma alanındaki erişimi etkilenir; kullanıcı hesabı silinmez."),
        reversible: tx("Evet. Kullanıcıyı yeniden ekleyebilirsiniz."),
      }}
      pending={remove.isPending} error={remove.error}
      onConfirm={() => { if (selected) remove.mutate(selected.userId, { onSuccess: () => setSelected(null) }) }} />
  </div>
}
