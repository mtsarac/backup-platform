import { tx, useLanguage } from '@/lib/i18n'
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
  { value: 'overview', get label() { return tx("Genel") } }, { value: 'members', get label() { return tx("Üyeler") } }, { value: 'subscription', get label() { return tx("Abonelik") } },
  { value: 'storage', get label() { return tx("Depolama") } }, { value: 'agents', get label() { return tx("Agent'lar") } }, { value: 'support', get label() { return tx("Destek erişimi") } },
] as const
type TabValue = (typeof tabs)[number]['value']

function RenameForm({ tenantId, name }: { tenantId: string; name: string }) {
  useLanguage()
  const [draft, setDraft] = useState(name)
  const [saved, setSaved] = useState(false)
  const rename = useRenameTenant(tenantId)
  const invalid = rename.error instanceof ApiError && rename.error.field === 'name'
  function submit(event: FormEvent) {
    event.preventDefault()
    setSaved(false)
    rename.mutate(draft.trim(), { onSuccess: () => setSaved(true) })
  }
  return <Section title={tx("Tenant adı")} description={tx("Ad tüm listelerde ve audit kayıtlarında görünür.")}>
    <form onSubmit={submit} className="space-y-3">
      <div className="flex flex-wrap items-end gap-3">
        <div className="min-w-0 flex-1 basis-64"><Field label={tx("Ad")}>{(control) => <input {...control} className={inputClass} value={draft} maxLength={200} required aria-invalid={invalid} onChange={(event) => { setSaved(false); setDraft(event.target.value) }} />}</Field></div>
        <Button type="submit" disabled={rename.isPending || !draft.trim() || draft.trim() === name}>{rename.isPending ? tx('Kaydediliyor…') : tx('Yeniden adlandır')}</Button>
      </div>
      {rename.error && <p role="alert" className="text-sm text-error">{errorMessage(rename.error)}</p>}
      {saved && <Notice tone="success">{tx("Tenant adı güncellendi.")}</Notice>}
    </form>
  </Section>
}

function EnrollmentSection({ tenant }: { tenant: PlatformTenantDetail }) {
  useLanguage()
  const [confirmOpen, setConfirmOpen] = useState(false)
  const change = useSetTenantEnrollment(tenant.tenantId)
  const enabled = tenant.enrollment.enabled
  return <Section title={tx("Agent kayıt politikası")} description={tx("Kaydı kapatmak mevcut Agent'ları iptal etmez; yalnızca yeni kayıtları engeller.")}
    actions={<Button role="switch" aria-checked={enabled} aria-label={tx("Yeni Agent kayıtlarına izin ver")} variant="outline" onClick={() => { change.reset(); setConfirmOpen(true) }}>{enabled ? tx('Kaydı kapat') : tx('Kaydı aç')}</Button>}>
    <p className="text-[0.9375rem]"><Badge tone={enabled ? 'success' : 'neutral'} dot>{enabled ? tx('Yeni Agent kaydı açık') : tx('Yeni Agent kaydı kapalı')}</Badge> <span className="ml-1 text-text-muted">{tenant.enrollment.changedAt ? tx("Son değişiklik: {0}", { "0": agentDate(tenant.enrollment.changedAt) }) : tx('Politika henüz hiç değiştirilmedi.')}</span></p>
    <ConfirmDialog open={confirmOpen} onClose={() => setConfirmOpen(false)} title={enabled ? tx('Agent kaydı kapatılsın mı?') : tx('Agent kaydı açılsın mı?')}
      description={enabled ? tx("“{0}” tenant'ında yeni Agent kayıtları engellenecek. Mevcut Agent'lar etkilenmez.", { "0": tenant.name }) : tx("“{0}” tenant'ında yeni Agent kayıtlarına izin verilecek.", { "0": tenant.name })}
      confirmLabel={enabled ? tx('Kaydı kapat') : tx('Kaydı aç')} pending={change.isPending} error={change.error ? errorMessage(change.error) : undefined}
      onConfirm={() => change.mutate(!enabled, { onSuccess: () => setConfirmOpen(false) })} />
  </Section>
}

function RecentAudit({ tenantId }: { tenantId: string }) {
  useLanguage()
  const query = usePlatformAudit({ tenantId, page: 1, pageSize: 10 })
  return <Section title={tx("Son audit kayıtları")} flush className="overflow-hidden" actions={<Link className={`${linkClass} text-sm`} to={platformPaths.audit(tenantId)}>{tx("Tüm audit kayıtları")}</Link>}>
    <PlatformQueryState isPending={query.isPending} error={query.error}>
      {query.data && <AuditTable className={bareTableClass} rows={query.data.items} showTenant={false} emptyMessage={tx("Bu tenant için audit kaydı yok.")} />}
    </PlatformQueryState>
  </Section>
}

function Header({ tenant }: { tenant: PlatformTenantDetail }) {
  useLanguage()
  const subscription = useTenantSubscription(tenant.tenantId).data?.subscription
  return <PageHeader crumbs={[platformCrumb, { label: tx('Tenants'), to: platformPaths.tenants }]} title={tenant.name}
    meta={<>
      <Badge tone={tenant.enrollment.enabled ? 'success' : 'neutral'} dot>{tenant.enrollment.enabled ? tx('Kayıt açık') : tx('Kayıt kapalı')}</Badge>
      {subscription && <span className="inline-flex flex-wrap items-center gap-1.5 text-sm text-text-muted">Plan: <span className="font-medium text-text">{subscription.planName}</span><SubscriptionStatusPill status={subscription.status} /></span>}
    </>}
    actions={<Link className={buttonVariants({ variant: 'outline' })} to={platformPaths.agents(tenant.tenantId)}>{tx("Bu tenant'ın Agent'ları")}</Link>} />
}

function TenantTabs({ tenant }: { tenant: PlatformTenantDetail }) {
  useLanguage()
  const { get, update } = useListParams()
  const requested = get('tab')
  const tab: TabValue = tabs.find((item) => item.value === requested)?.value ?? 'overview'
  return <div className="space-y-6">
    <Tabs label={tx("Tenant bölümleri")} idPrefix="tenant" value={tab} items={tabs} onChange={(value) => update({ tab: value === 'overview' ? '' : value, memberPage: '', subPage: '' })} />
    <div role="tabpanel" id={`tenant-panel-${tab}`} aria-labelledby={`tenant-tab-${tab}`} className="space-y-6">
      {tab === 'overview' && <>
        <div className="grid gap-4 sm:grid-cols-3"><KpiCard label={tx("Üyeler")} value={tenant.memberCount} /><KpiCard label={tx("Agents")} value={tenant.agents.total} /><KpiCard label={tx("Etkin Backup Jobs")} value={tenant.enabledBackupJobs} /></div>
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
        <Section title={tx("Agent durumu")} description={tx("Bağlantı kesilmesi ile devre dışı bırakma ayrı durumlardır.")} actions={<Link className={`${linkClass} text-sm`} to={platformPaths.agents(tenant.tenantId)}>{tx("Agent listesini aç")}</Link>}>
          <AgentBreakdown counts={tenant.agents} />
        </Section>
        <EnrollmentSection tenant={tenant} />
      </>}
      {tab === 'support' && <SupportAccessSection tenantId={tenant.tenantId} tenantName={tenant.name} />}
    </div>
  </div>
}

export function PlatformTenantDetailPage() {
  useLanguage()
  const { tenantId = '' } = useParams()
  const query = usePlatformTenant(tenantId)
  const tenant = query.data
  return <div className="space-y-6">
    {!tenant && <PageHeader crumbs={[platformCrumb, { label: tx('Tenants'), to: platformPaths.tenants }]} title={tx("Tenant")} />}
    <PlatformQueryState isPending={query.isPending} error={query.error}>
      {tenant && <>
        <Header tenant={tenant} />
        <TenantTabs tenant={tenant} />
      </>}
    </PlatformQueryState>
  </div>
}
