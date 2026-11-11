import { tx, useLanguage } from '@/lib/i18n'
import { useState, type FormEvent } from 'react'
import { Users } from 'lucide-react'
import { DataList, type Column } from '@/components/data/data-list'
import { Pagination } from '@/components/data/pagination'
import { Button } from '@/components/ui/button'
import { EmptyState } from '@/components/ui/empty-state'
import { Field } from '@/components/ui/field'
import { Notice } from '@/components/ui/notice'
import { Section } from '@/components/ui/section'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { errorMessage, platformOnlyMessage } from '@/lib/api/failure-messages'
import { useAddMembership, usePlatformMemberships, usePlatformUsers, useRemoveMembership } from './hooks'
import { bareListClass } from './list-empty'
import { SearchForm } from './search-form'
import type { PlatformMembership } from './types'
import { LIST_PAGE_SIZE, useListParams } from './use-list-params'
import { QueryState } from '@/components/data/query-state'
import { Select } from '@/components/ui/text-input'


function AddMemberForm({ tenantId }: { tenantId: string }) {
  useLanguage()
  const [userSearch, setUserSearch] = useState('')
  const [userId, setUserId] = useState('')
  const [added, setAdded] = useState('')
  const users = usePlatformUsers({ search: userSearch, page: 1, pageSize: 20 })
  const add = useAddMembership(tenantId)
  function submit(event: FormEvent) {
    event.preventDefault()
    setAdded('')
    const chosen = users.data?.items.find((user) => user.userId === userId)
    add.mutate({ userId }, { onSuccess: () => { setUserId(''); setAdded(chosen?.displayName ?? '') } })
  }
  return <Section title={tx("Üye ekle")} description={tx("Var olan bir kullanıcıyı bu tenant'a ekleyin. Kullanıcı hesabı burada oluşturulmaz.")}>
    <div className="space-y-4">
      <div className="flex max-w-xl"><SearchForm label={tx("Kullanıcı ara")} value={userSearch} onSearch={(value) => { setUserSearch(value); setUserId('') }} placeholder={tx("Ad veya kullanıcı adı")} /></div>
      <form onSubmit={submit} className="grid items-end gap-4 sm:grid-cols-[minmax(0,1fr)_10rem_auto]">
        <Field label={tx("Kullanıcı")}>{(control) => <Select {...control} value={userId} onChange={(event) => setUserId(event.target.value)} disabled={users.isPending}>
          <option value="">{users.isPending ? tx('Kullanıcılar yükleniyor…') : users.error ? tx('Kullanıcılar alınamadı') : tx('Kullanıcı seçin')}</option>
          {users.data?.items.map((user) => <option key={user.userId} value={user.userId}>{user.displayName}{user.loginIdentifier ? ` (${user.loginIdentifier})` : ''}{user.isPlatformAdmin ? tx(' · PlatformAdmin') : ''}</option>)}
        </Select>}</Field>
        <Button type="submit" disabled={!userId || add.isPending}>{add.isPending ? tx('Ekleniyor…') : tx('Üye ekle')}</Button>
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
  const query = usePlatformMemberships(tenantId, { page, pageSize: LIST_PAGE_SIZE })
  const remove = useRemoveMembership(tenantId)
  const [selected, setSelected] = useState<PlatformMembership | null>(null)
  const data = query.data
  const columns: Column<PlatformMembership>[] = [
    { key: 'name', header: tx('Ad'), wrap: true, render: (member) => <span className="font-medium">{member.displayName}</span> },
    { key: 'login', header: tx('Kullanıcı adı'), wrap: true, render: (member) => member.loginIdentifier ?? '—' },
    { key: 'actions', card: 'actions', header: tx('İşlemler'), render: (member) => <Button variant="destructive" size="sm" aria-label={tx("{0} üyeliğini kaldır", { "0": member.displayName })} onClick={() => { remove.reset(); setSelected(member) }}>{tx("Kaldır")}</Button> },
  ]
  return <div className="space-y-6">
    <AddMemberForm tenantId={tenantId} />
    <Section title={tx("Üyelikler")} description={tx("Üyeliği kaldırmak kullanıcı hesabını silmez.")} flush className="overflow-hidden">
      <QueryState forbiddenMessage={platformOnlyMessage} isPending={query.isPending} error={query.error}>
        {data && (data.totalCount === 0
          ? <EmptyState icon={Users} message={tx("Bu tenant'ın henüz üyesi yok; yukarıdan bir kullanıcı ekleyin.")} />
          : <DataList className={bareListClass} rows={data.items} columns={columns} keyExtractor={(member) => member.userId} emptyMessage={tx("Bu tenant'ın henüz üyesi yok.")} />)}
      </QueryState>
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
