import { useState, type FormEvent } from 'react'
import { Link, useParams } from 'react-router-dom'
import { KpiCard } from '@/components/data/kpi-card'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { buttonVariants } from '@/components/ui/button-variants'
import { Field } from '@/components/ui/field'
import { Notice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { Section } from '@/components/ui/section'
import { Tabs } from '@/components/ui/tabs'
import { agentDate } from '@/features/agents/format'
import { ConfirmDialog } from '@/features/agents/confirm-dialog'
import { AgentBreakdown } from '@/features/platform/agent-breakdown'
import { AuditTable } from '@/features/platform/audit-table'
import { platformCrumb } from '@/features/platform/crumbs'
import { usePlatformAudit, usePlatformTenant, useRenameTenant, useSetTenantEnrollment } from '@/features/platform/hooks'
import { bareTableClass } from '@/features/platform/list-empty'
import { platformPaths } from '@/features/platform/paths'
import { useTenantSubscription } from '@/features/platform/plans/hooks'
import { SubscriptionSection } from '@/features/platform/plans/subscription-section'
import { SubscriptionStatusPill } from '@/features/platform/plans/subscription-presentation'
import { PlatformQueryState } from '@/features/platform/platform-query-state'
import { TenantStorageSection } from '@/features/platform/storage/tenant-storage-section'
import { inputClass, linkClass } from '@/features/platform/styles'
import { SupportAccessSection } from '@/features/platform/support/support-access-section'
import { MembershipsSection } from '@/features/platform/tenant-members'
import type { PlatformTenantDetail } from '@/features/platform/types'
import { useListParams } from '@/features/platform/use-list-params'
import { PlatformTenantConnections } from '@/features/storage-connections/platform-tenant-connections'
import { ApiError } from '@/lib/api/client'
import { errorMessage } from '@/lib/api/failure-messages'

const tabs = [
  { value: 'overview', label: 'Genel' }, { value: 'members', label: 'Üyeler' }, { value: 'subscription', label: 'Abonelik' },
  { value: 'storage', label: 'Depolama' }, { value: 'agents', label: "Agent'lar" }, { value: 'support', label: 'Destek erişimi' },
] as const
type TabValue = (typeof tabs)[number]['value']

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
  return <Section title="Tenant adı" description="Ad tüm listelerde ve audit kayıtlarında görünür.">
    <form onSubmit={submit} className="space-y-3">
      <div className="flex flex-wrap items-end gap-3">
        <div className="min-w-0 flex-1 basis-64"><Field label="Ad">{(control) => <input {...control} className={inputClass} value={draft} maxLength={200} required aria-invalid={invalid} onChange={(event) => { setSaved(false); setDraft(event.target.value) }} />}</Field></div>
        <Button type="submit" disabled={rename.isPending || !draft.trim() || draft.trim() === name}>{rename.isPending ? 'Kaydediliyor…' : 'Yeniden adlandır'}</Button>
      </div>
      {rename.error && <p role="alert" className="text-sm text-error">{errorMessage(rename.error)}</p>}
      {saved && <Notice tone="success">Tenant adı güncellendi.</Notice>}
    </form>
  </Section>
}

function EnrollmentSection({ tenant }: { tenant: PlatformTenantDetail }) {
  const [confirmOpen, setConfirmOpen] = useState(false)
  const change = useSetTenantEnrollment(tenant.tenantId)
  const enabled = tenant.enrollment.enabled
  return <Section title="Agent kayıt politikası" description="Kaydı kapatmak mevcut Agent'ları iptal etmez; yalnızca yeni kayıtları engeller."
    actions={<Button role="switch" aria-checked={enabled} aria-label="Yeni Agent kayıtlarına izin ver" variant="outline" onClick={() => { change.reset(); setConfirmOpen(true) }}>{enabled ? 'Kaydı kapat' : 'Kaydı aç'}</Button>}>
    <p className="text-[0.9375rem]"><Badge tone={enabled ? 'success' : 'neutral'} dot>{enabled ? 'Yeni Agent kaydı açık' : 'Yeni Agent kaydı kapalı'}</Badge> <span className="ml-1 text-text-muted">{tenant.enrollment.changedAt ? `Son değişiklik: ${agentDate(tenant.enrollment.changedAt)}` : 'Politika henüz hiç değiştirilmedi.'}</span></p>
    <ConfirmDialog open={confirmOpen} onClose={() => setConfirmOpen(false)} title={enabled ? 'Agent kaydı kapatılsın mı?' : 'Agent kaydı açılsın mı?'}
      description={enabled ? `“${tenant.name}” tenant'ında yeni Agent kayıtları engellenecek. Mevcut Agent'lar etkilenmez.` : `“${tenant.name}” tenant'ında yeni Agent kayıtlarına izin verilecek.`}
      confirmLabel={enabled ? 'Kaydı kapat' : 'Kaydı aç'} pending={change.isPending} error={change.error ? errorMessage(change.error) : undefined}
      onConfirm={() => change.mutate(!enabled, { onSuccess: () => setConfirmOpen(false) })} />
  </Section>
}

function RecentAudit({ tenantId }: { tenantId: string }) {
  const query = usePlatformAudit({ tenantId, page: 1, pageSize: 10 })
  return <Section title="Son audit kayıtları" flush className="overflow-hidden" actions={<Link className={`${linkClass} text-sm`} to={platformPaths.audit(tenantId)}>Tüm audit kayıtları</Link>}>
    <PlatformQueryState isPending={query.isPending} error={query.error}>
      {query.data && <AuditTable className={bareTableClass} rows={query.data.items} showTenant={false} emptyMessage="Bu tenant için audit kaydı yok." />}
    </PlatformQueryState>
  </Section>
}

function Header({ tenant }: { tenant: PlatformTenantDetail }) {
  const subscription = useTenantSubscription(tenant.tenantId).data?.subscription
  return <PageHeader crumbs={[platformCrumb, { label: 'Tenants', to: platformPaths.tenants }]} title={tenant.name}
    meta={<>
      <Badge tone={tenant.enrollment.enabled ? 'success' : 'neutral'} dot>{tenant.enrollment.enabled ? 'Kayıt açık' : 'Kayıt kapalı'}</Badge>
      {subscription && <span className="inline-flex flex-wrap items-center gap-1.5 text-sm text-text-muted">Plan: <span className="font-medium text-text">{subscription.planName}</span><SubscriptionStatusPill status={subscription.status} /></span>}
    </>}
    actions={<Link className={buttonVariants({ variant: 'outline' })} to={platformPaths.agents(tenant.tenantId)}>Bu tenant'ın Agent'ları</Link>} />
}

function TenantTabs({ tenant }: { tenant: PlatformTenantDetail }) {
  const { get, update } = useListParams()
  const requested = get('tab')
  const tab: TabValue = tabs.find((item) => item.value === requested)?.value ?? 'overview'
  return <div className="space-y-6">
    <Tabs label="Tenant bölümleri" idPrefix="tenant" value={tab} items={tabs} onChange={(value) => update({ tab: value === 'overview' ? '' : value, memberPage: '', subPage: '' })} />
    <div role="tabpanel" id={`tenant-panel-${tab}`} aria-labelledby={`tenant-tab-${tab}`} className="space-y-6">
      {tab === 'overview' && <>
        <div className="grid gap-4 sm:grid-cols-3"><KpiCard label="Üyeler" value={tenant.memberCount} /><KpiCard label="Agents" value={tenant.agents.total} /><KpiCard label="Etkin Backup Jobs" value={tenant.enabledBackupJobs} /></div>
        <RenameForm key={tenant.name} tenantId={tenant.tenantId} name={tenant.name} />
        <RecentAudit tenantId={tenant.tenantId} />
      </>}
      {tab === 'members' && <MembershipsSection tenantId={tenant.tenantId} tenantName={tenant.name} />}
      {tab === 'subscription' && <SubscriptionSection tenantId={tenant.tenantId} tenantName={tenant.name} />}
      {tab === 'storage' && <>
        <TenantStorageSection tenantId={tenant.tenantId} tenantName={tenant.name} />
        <PlatformTenantConnections tenantId={tenant.tenantId} tenantName={tenant.name} />
      </>}
      {tab === 'agents' && <>
        <Section title="Agent durumu" description="Bağlantı kesilmesi ile devre dışı bırakma ayrı durumlardır." actions={<Link className={`${linkClass} text-sm`} to={platformPaths.agents(tenant.tenantId)}>Agent listesini aç</Link>}>
          <AgentBreakdown counts={tenant.agents} />
        </Section>
        <EnrollmentSection tenant={tenant} />
      </>}
      {tab === 'support' && <SupportAccessSection tenantId={tenant.tenantId} tenantName={tenant.name} />}
    </div>
  </div>
}

export function PlatformTenantDetailPage() {
  const { tenantId = '' } = useParams()
  const query = usePlatformTenant(tenantId)
  const tenant = query.data
  return <div className="space-y-6">
    {!tenant && <PageHeader crumbs={[platformCrumb, { label: 'Tenants', to: platformPaths.tenants }]} title="Tenant" />}
    <PlatformQueryState isPending={query.isPending} error={query.error}>
      {tenant && <>
        <Header tenant={tenant} />
        <TenantTabs tenant={tenant} />
      </>}
    </PlatformQueryState>
  </div>
}
