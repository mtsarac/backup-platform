import { tx, useLanguage } from '@/lib/i18n'
import { useState, type FormEvent } from 'react'
import { Link, useParams } from 'react-router-dom'
import { KpiCard } from '@/components/data/kpi-card'
import { Button } from '@/components/ui/button'
import { buttonVariants } from '@/components/ui/button-variants'
import { Field } from '@/components/ui/field'
import { Notice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { Section } from '@/components/ui/section'
import { Tabs } from '@/components/ui/tabs'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { AgentBreakdown } from '@/features/platform/agent-breakdown'
import { AuditTable } from '@/features/platform/audit-table'
import { platformCrumb } from '@/features/platform/crumbs'
import { usePlatformAudit, usePlatformTenant, useRenameTenant, useSetTenantEnrollment } from '@/features/platform/hooks'
import { bareListClass } from '@/features/platform/list-empty'
import { platformPaths } from '@/features/platform/paths'
import { PlatformQueryState } from '@/features/platform/platform-query-state'
import { TenantStorageSection } from '@/features/platform/storage/tenant-storage-section'
import { inputClass, linkClass } from '@/components/ui/form-styles'
import { SupportAccessSection } from '@/features/platform/support/support-access-section'
import { MembershipsSection } from '@/features/platform/tenant-members'
import type { PlatformTenantDetail } from '@/features/platform/types'
import { useListParams } from '@/features/platform/use-list-params'
import { PlatformTenantConnections } from '@/features/storage-connections/platform-tenant-connections'
import { ApiError } from '@/lib/api/client'
import { errorMessage } from '@/lib/api/failure-messages'
import { WrapKeysSection } from '@/features/artifact-encryption/wrap-keys-section'
import { formatDateTime } from '@/lib/format'
import { StatusPill } from '@/components/data/status-pill'
import { Pagination } from '@/components/data/pagination'
import { RelativeTime } from '@/components/data/relative-time'
import { TechnicalDetails } from '@/components/ui/technical-details'
import { useBreakGlassRequests, useTenantEncryption } from '@/features/artifact-encryption/hooks'
import { BreakGlassTable } from '@/features/artifact-encryption/break-glass-table'
import { TenantKeysSection } from '@/features/artifact-encryption/tenant-recovery-key-section'
import { PLATFORM_PAGE_SIZE } from '@/features/platform/use-list-params'

const tabs = [
  { value: 'summary', get label() { return tx("Özet") } },
  { value: 'people', get label() { return tx("Kişiler") } },
  { value: 'storage-computers', get label() { return tx("Depolama ve bilgisayarlar") } },
  { value: 'security', get label() { return tx("Güvenlik") } },
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
  return <Section title={tx("Yedekleme bilgisayarı kaydı")} description={tx("Kaydı kapatmak mevcut yedekleme bilgisayarlarını etkilemez; yalnızca yeni kayıtları engeller.")}
    actions={<Button role="switch" aria-checked={enabled} aria-label={tx("Yeni yedekleme bilgisayarı kayıtlarına izin ver")} variant="outline" onClick={() => { change.reset(); setConfirmOpen(true) }}>{enabled ? tx('Kaydı kapat') : tx('Kaydı aç')}</Button>}>
    <p className="text-[0.9375rem]"><StatusPill level={enabled ? 'safe' : 'neutral'}>{enabled ? tx('Yeni bilgisayar kaydı açık') : tx('Yeni bilgisayar kaydı kapalı')}</StatusPill>{tenant.enrollment.changedAt && <span className="ml-2 text-text-muted"><RelativeTime value={tenant.enrollment.changedAt} /></span>}</p>
    <ConfirmDialog open={confirmOpen} onClose={() => setConfirmOpen(false)} title={enabled ? tx('Bilgisayar kaydı kapatılsın mı?') : tx('Bilgisayar kaydı açılsın mı?')}
      consequence={{
        what: enabled ? tx("“{0}” çalışma alanında yeni yedekleme bilgisayarı kayıtları engellenir.", { "0": tenant.name }) : tx("“{0}” çalışma alanında yeni yedekleme bilgisayarı kayıtlarına izin verilir.", { "0": tenant.name }),
        who: tx("Mevcut yedekleme bilgisayarları etkilenmez; yalnızca yeni kayıtlar etkilenir."),
        reversible: tx("Evet. Kaydı istediğiniz zaman yeniden açabilir veya kapatabilirsiniz."),
      }}
      confirmLabel={enabled ? tx('Kaydı kapat') : tx('Kaydı aç')} pending={change.isPending} error={change.error}
      onConfirm={() => change.mutate(!enabled, { onSuccess: () => setConfirmOpen(false) })} />
  </Section>
}

function RecentAudit({ tenantId }: { tenantId: string }) {
  useLanguage()
  const query = usePlatformAudit({ tenantId, page: 1, pageSize: 10 })
  return <Section title={tx("Son audit kayıtları")} flush className="overflow-hidden" actions={<Link className={`${linkClass} text-sm`} to={platformPaths.audit(tenantId)}>{tx("Tüm audit kayıtları")}</Link>}>
    <PlatformQueryState isPending={query.isPending} error={query.error}>
      {query.data && <AuditTable className={bareListClass} rows={query.data.items} showTenant={false} emptyMessage={tx("Bu tenant için audit kaydı yok.")} />}
    </PlatformQueryState>
  </Section>
}

function Header({ tenant }: { tenant: PlatformTenantDetail }) {
  useLanguage()
  return <PageHeader crumbs={[platformCrumb, { label: tx('Tenants'), to: platformPaths.tenants }]} title={tenant.name}
    meta={<>
      <StatusPill level={tenant.enrollment.enabled ? 'safe' : 'neutral'}>{tenant.enrollment.enabled ? tx('Kayıt açık') : tx('Kayıt kapalı')}</StatusPill>
    </>}
    actions={<Link className={buttonVariants({ variant: 'outline' })} to={platformPaths.agents(tenant.tenantId)}>{tx("Yedekleme bilgisayarlarını görüntüle")}</Link>} />
}

function RecoveryKeys({ tenant }: { tenant: PlatformTenantDetail }) {
  const encryption = useTenantEncryption(tenant.tenantId)
  return <PlatformQueryState isPending={encryption.isPending} error={encryption.error}>
    {encryption.data && <TenantKeysSection tenantId={tenant.tenantId} tenantName={tenant.name} encryption={encryption.data} />}
  </PlatformQueryState>
}

function EmergencyAccessHistory({ tenantId }: { tenantId: string }) {
  useLanguage()
  const { page, update } = useListParams('breakGlassPage')
  const query = useBreakGlassRequests({ tenantId, page, pageSize: PLATFORM_PAGE_SIZE })
  return <Section title={tx('Acil destek erişimi geçmişi')}>
    <PlatformQueryState isPending={query.isPending} error={query.error}>
      {query.data && <div className="space-y-4">
        <BreakGlassTable rows={query.data.items} />
        {query.data.totalCount > 0 && <Pagination label={tx('Acil destek erişimi sayfaları')} page={page} pageSize={query.data.pageSize} totalCount={query.data.totalCount} disabled={query.isFetching} onPageChange={next => update({ breakGlassPage: next })} />}
      </div>}
    </PlatformQueryState>
  </Section>
}

function TenantTabs({ tenant }: { tenant: PlatformTenantDetail }) {
  useLanguage()
  const { get, update } = useListParams()
  const requested = get('tab')
  const tab: TabValue = tabs.find((item) => item.value === requested)?.value ?? 'summary'
  return <div className="space-y-6">
    <Tabs label={tx("Tenant bölümleri")} idPrefix="tenant" value={tab} items={tabs} onChange={(value) => update({ tab: value, memberPage: '', subPage: '', breakGlassPage: '' })} />
    <div role="tabpanel" id={`tenant-panel-${tab}`} aria-labelledby={`tenant-tab-${tab}`} className="space-y-6">
      {tab === 'summary' && <>
        <div className="grid gap-4 sm:grid-cols-3"><KpiCard label={tx("Kişiler")} value={tenant.memberCount} /><KpiCard label={tx("Yedekleme bilgisayarları")} value={tenant.agents.total} /><KpiCard label={tx("Etkin yedekleme işleri")} value={tenant.enabledBackupJobs} /></div>
        <RenameForm key={tenant.name} tenantId={tenant.tenantId} name={tenant.name} />
        <RecentAudit tenantId={tenant.tenantId} />
      </>}
      {tab === 'people' && <MembershipsSection tenantId={tenant.tenantId} tenantName={tenant.name} />}
      {tab === 'storage-computers' && <>
        <TenantStorageSection tenantId={tenant.tenantId} tenantName={tenant.name} />
        <PlatformTenantConnections tenantId={tenant.tenantId} tenantName={tenant.name} />
        <Section title={tx("Yedekleme bilgisayarlarının durumu")} actions={<Link className={`${linkClass} inline-flex min-h-11 items-center text-sm`} to={platformPaths.agents(tenant.tenantId)}>{tx("Yedekleme bilgisayarlarını görüntüle")}</Link>}>
          <AgentBreakdown counts={tenant.agents} />
        </Section>
        <EnrollmentSection tenant={tenant} />
        <TechnicalDetails items={[
          { label: tx('Tenant kimliği'), value: tenant.tenantId },
          { label: tx('Son değişiklik'), value: formatDateTime(tenant.enrollment.changedAt) },
        ]} />
      </>}
      {tab === 'security' && <>
        <SupportAccessSection tenantId={tenant.tenantId} tenantName={tenant.name} />
        <WrapKeysSection tenantId={tenant.tenantId} tenantName={tenant.name} />
        <RecoveryKeys tenant={tenant} />
        <EmergencyAccessHistory tenantId={tenant.tenantId} />
      </>}
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
