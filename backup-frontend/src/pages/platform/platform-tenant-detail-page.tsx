import { useState, type FormEvent } from 'react'
import { Link, useParams } from 'react-router-dom'
import { DataTable, type Column } from '@/components/data/data-table'
import { Pagination } from '@/components/data/pagination'
import { KpiCard } from '@/components/data/kpi-card'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { agentDate } from '@/features/agents/format'
import { ConfirmDialog } from '@/features/agents/confirm-dialog'
import { AgentBreakdown } from '@/features/platform/agent-breakdown'
import { AuditTable } from '@/features/platform/audit-table'
import {
  useAddMembership, useChangeMembershipRole, usePlatformAudit, usePlatformMemberships, usePlatformTenant, usePlatformUsers,
  useRemoveMembership, useRenameTenant, useSetTenantEnrollment,
} from '@/features/platform/hooks'
import { SubscriptionSection } from '@/features/platform/plans/subscription-section'
import { TenantStorageSection } from '@/features/platform/storage/tenant-storage-section'
import { PlatformTenantConnections } from '@/features/storage-connections/platform-tenant-connections'
import { SupportAccessSection } from '@/features/platform/support/support-access-section'
import { platformPaths } from '@/features/platform/paths'
import { PlatformQueryState } from '@/features/platform/platform-query-state'
import { SearchForm } from '@/features/platform/search-form'
import { inlineSelectClass, inputClass, linkClass, selectClass } from '@/features/platform/styles'
import { membershipRoles, type MembershipRole, type PlatformMembership, type PlatformTenantDetail } from '@/features/platform/types'
import { PLATFORM_PAGE_SIZE, useListParams } from '@/features/platform/use-list-params'
import { ApiError } from '@/lib/api/client'
import { errorMessage } from '@/lib/api/failure-messages'

function isRole(value: string): value is MembershipRole {
  return membershipRoles.some((role) => role === value)
}

function RenameForm({ tenantId, name }: { tenantId: string; name: string }) {
  const [draft, setDraft] = useState(name)
  const [saved, setSaved] = useState(false)
  const rename = useRenameTenant(tenantId)
  const invalid = rename.error instanceof ApiError && rename.error.field === 'name'
  function submit(event: FormEvent) {
    event.preventDefault()
    setSaved(false)
    rename.mutate(draft.trim(), { onSuccess: () => setSaved(true) })
  }
  return <form onSubmit={submit} className="space-y-3 rounded-lg border border-border bg-surface p-4">
    <h2 className="font-medium text-text-heading">Tenant adı</h2>
    <div className="flex flex-wrap items-end gap-3">
      <label className="min-w-0 flex-1 text-sm text-text-muted">Ad<input className={inputClass} value={draft} maxLength={200} required aria-invalid={invalid} onChange={(event) => { setSaved(false); setDraft(event.target.value) }} /></label>
      <Button type="submit" disabled={rename.isPending || !draft.trim() || draft.trim() === name}>{rename.isPending ? 'Kaydediliyor…' : 'Yeniden adlandır'}</Button>
    </div>
    {rename.error && <p role="alert" className="text-sm text-error">{errorMessage(rename.error)}</p>}
    {saved && <p role="status" className="text-sm text-success">Tenant adı güncellendi.</p>}
  </form>
}

function EnrollmentSection({ tenant }: { tenant: PlatformTenantDetail }) {
  const [confirmOpen, setConfirmOpen] = useState(false)
  const change = useSetTenantEnrollment(tenant.tenantId)
  const enabled = tenant.enrollment.enabled
  return <section className="space-y-3 rounded-lg border border-border bg-surface p-4">
    <div className="flex flex-wrap items-center justify-between gap-3">
      <div><h2 className="font-medium text-text-heading">Agent kayıt politikası</h2><p className="mt-1 text-sm text-text-muted">{enabled ? 'Yeni Agent kaydı açık.' : 'Yeni Agent kaydı kapalı.'} {tenant.enrollment.changedAt ? `Son değişiklik: ${agentDate(tenant.enrollment.changedAt)}` : 'Politika henüz hiç değiştirilmedi.'}</p></div>
      <Button role="switch" aria-checked={enabled} aria-label="Yeni Agent kayıtlarına izin ver" variant="outline" onClick={() => { change.reset(); setConfirmOpen(true) }}>{enabled ? 'Kaydı kapat' : 'Kaydı aç'}</Button>
    </div>
    <p className="text-xs text-text-muted">Kaydı kapatmak mevcut Agent'ları iptal etmez; yeni kayıtları engeller.</p>
    <ConfirmDialog open={confirmOpen} onClose={() => setConfirmOpen(false)} title={enabled ? 'Agent kaydı kapatılsın mı?' : 'Agent kaydı açılsın mı?'}
      description={enabled ? `“${tenant.name}” tenant'ında yeni Agent kayıtları engellenecek. Mevcut Agent'lar etkilenmez.` : `“${tenant.name}” tenant'ında yeni Agent kayıtlarına izin verilecek.`}
      confirmLabel={enabled ? 'Kaydı kapat' : 'Kaydı aç'} pending={change.isPending} error={change.error ? errorMessage(change.error) : undefined}
      onConfirm={() => change.mutate(!enabled, { onSuccess: () => setConfirmOpen(false) })} />
  </section>
}

function AddMemberForm({ tenantId }: { tenantId: string }) {
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
  return <div className="space-y-3 rounded-lg border border-border bg-surface p-4">
    <h3 className="font-medium text-text-heading">Üye ekle</h3>
    <div className="flex flex-wrap items-end gap-3">
      <SearchForm label="Kullanıcı ara" value={userSearch} onSearch={(value) => { setUserSearch(value); setUserId('') }} placeholder="Ad veya kullanıcı adı" />
    </div>
    <form onSubmit={submit} className="flex flex-wrap items-end gap-3">
      <label className="min-w-48 flex-1 text-sm text-text-muted">Kullanıcı
        <select className={`${selectClass} w-full`} value={userId} onChange={(event) => setUserId(event.target.value)} disabled={users.isPending}>
          <option value="">{users.isPending ? 'Kullanıcılar yükleniyor…' : users.error ? 'Kullanıcılar alınamadı' : 'Kullanıcı seçin'}</option>
          {users.data?.items.map((user) => <option key={user.userId} value={user.userId}>{user.displayName}{user.loginIdentifier ? ` (${user.loginIdentifier})` : ''}{user.isPlatformAdmin ? ' · PlatformAdmin' : ''}</option>)}
        </select>
      </label>
      <label className="text-sm text-text-muted">Rol<select className={selectClass} value={role} onChange={(event) => { if (isRole(event.target.value)) setRole(event.target.value) }}>{membershipRoles.map((item) => <option key={item} value={item}>{item}</option>)}</select></label>
      <Button type="submit" disabled={!userId || add.isPending}>{add.isPending ? 'Ekleniyor…' : 'Üye ekle'}</Button>
    </form>
    {users.error && <p role="alert" className="text-sm text-error">{errorMessage(users.error)}</p>}
    {users.data && users.data.totalCount > users.data.items.length && <p className="text-xs text-text-muted">İlk {users.data.items.length} kullanıcı gösteriliyor; listeyi daraltmak için arayın.</p>}
    {add.error && <p role="alert" className="text-sm text-error">{errorMessage(add.error)}</p>}
    {added && <p role="status" className="text-sm text-success">“{added}” tenant'a eklendi.</p>}
  </div>
}

function MembershipsSection({ tenantId, tenantName }: { tenantId: string; tenantName: string }) {
  const { page, update } = useListParams('memberPage')
  const query = usePlatformMemberships(tenantId, { page, pageSize: PLATFORM_PAGE_SIZE })
  const changeRole = useChangeMembershipRole(tenantId)
  const remove = useRemoveMembership(tenantId)
  const [selected, setSelected] = useState<PlatformMembership | null>(null)
  const data = query.data
  const columns: Column<PlatformMembership>[] = [
    { key: 'name', header: 'Ad', wrap: true, render: (member) => <span className="font-medium">{member.displayName}</span> },
    { key: 'login', header: 'Kullanıcı adı', wrap: true, render: (member) => member.loginIdentifier ?? '—' },
    { key: 'role', header: 'Rol', render: (member) => <select aria-label={`${member.displayName} rolü`} className={inlineSelectClass} value={member.role} disabled={changeRole.isPending} onChange={(event) => { if (isRole(event.target.value)) changeRole.mutate({ userId: member.userId, role: event.target.value }) }}>{membershipRoles.map((role) => <option key={role} value={role}>{role}</option>)}</select> },
    { key: 'actions', header: 'İşlemler', render: (member) => <Button variant="destructive" size="sm" aria-label={`${member.displayName} üyeliğini kaldır`} onClick={() => { remove.reset(); setSelected(member) }}>Kaldır</Button> },
  ]
  return <section className="space-y-3">
    <h2 className="text-lg font-medium text-text-heading">Üyelikler</h2>
    <AddMemberForm tenantId={tenantId} />
    {changeRole.error && <p role="alert" className="text-sm text-error">{errorMessage(changeRole.error)}</p>}
    <PlatformQueryState isPending={query.isPending} error={query.error}>
      {data && <>
        <DataTable rows={data.items} columns={columns} keyExtractor={(member) => member.userId} emptyMessage="Bu tenant'ın henüz üyesi yok." />
        {data.totalCount > 0 && <Pagination label="Üyelik sayfaları" page={page} pageSize={data.pageSize} totalCount={data.totalCount} disabled={query.isFetching} onPageChange={(next) => update({ memberPage: next })} />}
      </>}
    </PlatformQueryState>
    <ConfirmDialog open={Boolean(selected)} onClose={() => setSelected(null)} title="Üyelik kaldırılsın mı?" confirmLabel="Üyeliği kaldır"
      description={selected ? `${selected.displayName} kullanıcısı “${tenantName}” tenant'ından çıkarılacak. Kullanıcı hesabı silinmez.` : ''}
      pending={remove.isPending} error={remove.error ? errorMessage(remove.error) : undefined}
      onConfirm={() => { if (selected) remove.mutate(selected.userId, { onSuccess: () => setSelected(null) }) }} />
  </section>
}

function RecentAudit({ tenantId }: { tenantId: string }) {
  const query = usePlatformAudit({ tenantId, page: 1, pageSize: 10 })
  return <section className="space-y-3">
    <div className="flex flex-wrap items-center justify-between gap-2"><h2 className="text-lg font-medium text-text-heading">Son audit kayıtları</h2><Link className={`${linkClass} text-sm`} to={platformPaths.audit(tenantId)}>Tüm audit kayıtları</Link></div>
    <PlatformQueryState isPending={query.isPending} error={query.error}>
      {query.data && <AuditTable rows={query.data.items} showTenant={false} emptyMessage="Bu tenant için audit kaydı yok." />}
    </PlatformQueryState>
  </section>
}

export function PlatformTenantDetailPage() {
  const { tenantId = '' } = useParams()
  const query = usePlatformTenant(tenantId)
  const tenant = query.data
  return <div className="space-y-6">
    <Link to={platformPaths.tenants} className="text-sm font-medium text-primary hover:underline">← Tenants'a dön</Link>
    <PlatformQueryState isPending={query.isPending} error={query.error}>
      {tenant && <>
        <div className="flex flex-wrap items-center justify-between gap-3">
          <h1 className="min-w-0 break-words text-2xl font-semibold text-text-heading">{tenant.name}</h1>
          <Link className="rounded-lg border border-border bg-surface px-3 py-2 text-sm font-medium text-primary hover:bg-surface-2" to={platformPaths.agents(tenant.tenantId)}>Bu tenant'ın Agent'ları</Link>
        </div>
        <div className="grid gap-4 sm:grid-cols-3"><KpiCard label="Üyeler" value={tenant.memberCount} /><KpiCard label="Agents" value={tenant.agents.total} /><KpiCard label="Etkin Backup Jobs" value={tenant.enabledBackupJobs} /></div>
        <section className="space-y-3 rounded-lg border border-border bg-surface p-4"><div className="flex flex-wrap items-center gap-2"><h2 className="font-medium text-text-heading">Agent durumu</h2><Badge tone={tenant.enrollment.enabled ? 'success' : 'neutral'}>{tenant.enrollment.enabled ? 'Kayıt açık' : 'Kayıt kapalı'}</Badge></div><AgentBreakdown counts={tenant.agents} /></section>
        <RenameForm key={tenant.name} tenantId={tenant.tenantId} name={tenant.name} />
        <EnrollmentSection tenant={tenant} />
        <SubscriptionSection tenantId={tenant.tenantId} tenantName={tenant.name} />
        <TenantStorageSection tenantId={tenant.tenantId} tenantName={tenant.name} />
        <PlatformTenantConnections tenantId={tenant.tenantId} tenantName={tenant.name} />
        <SupportAccessSection tenantId={tenant.tenantId} tenantName={tenant.name} />
        <MembershipsSection tenantId={tenant.tenantId} tenantName={tenant.name} />
        <RecentAudit tenantId={tenant.tenantId} />
      </>}
    </PlatformQueryState>
  </div>
}
