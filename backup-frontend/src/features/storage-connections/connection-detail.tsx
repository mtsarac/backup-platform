import { tx, useLanguage } from '@/lib/i18n'
import { useEffect } from 'react'
import { Link, useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { DataList } from '@/components/data/data-list'
import { QueryState } from '@/components/data/query-state'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { buttonVariants } from '@/components/ui/button-variants'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { Fact, Facts, Section } from '@/components/ui/section'
import { Tabs } from '@/components/ui/tabs'
import { TechnicalDetails } from '@/components/ui/technical-details'
import { CopyButton } from '@/components/ui/copy-button'
import { useTenant } from '@/features/tenancy/use-tenant'
import { ConnectionActions } from './connection-actions'
import { CapabilityChips, CapacityPill, HealthBadge, SecretStatus } from './connection-parts'
import { useTenantConnectionCapacity, useProbeTenantConnectionCapacity, useTenantConnectionOps, useTenantConnections, useTenantProviders } from './hooks'
import { configurationSummary, connectionHealth, disableEffects, enableEffect, footprintItems, platformLockState, executionLocationLabel, testMessage, testModeLabel, topologyLabel } from './labels'
import type { ConnectionView, ProviderDescriptor } from './types'
import { formatBytes, formatDateTime, formatRelativeTime } from '@/lib/format'
import { ProgressBar } from '@/components/data/progress-bar'
import { StatusPill } from '@/components/data/status-pill'

const tabs = [
  { value: 'overview', get label() { return tx("Genel bakış") } },
  { value: 'configuration', get label() { return tx("Yapılandırma") } },
  { value: 'credentials', get label() { return tx("Kimlik bilgisi") } },
  { value: 'manage', get label() { return tx("Yönetim") } },
] as const
type TabValue = (typeof tabs)[number]['value']

/** The headline finding for the connection, worded for what the person should do next. */
function HealthNotice({ connection }: { connection: ConnectionView }) {
  useLanguage()
  const health = connectionHealth(connection)
  const test = connection.lastTest
  if (health.key === 'disabled') return <Notice tone="warning" title={tx("Bu hedef devre dışı")}>{tx("Yeni backup başlatılamaz. Mevcut backup'lar indirilebilir ve geri yüklenebilir kalır.")}</Notice>
  if (health.key === 'reauthorize') return <Notice tone="warning" title={tx("Google hesabının yeniden yetkilendirilmesi gerekiyor")}>{tx("Kimlik bilgisi sekmesinden hesabı yeniden bağlayın; bağlanana kadar bu hedefe erişen işlemler başarısız olabilir.")}</Notice>
  if (health.key === 'failed' && test) return <ErrorNotice title={tx("Son test başarısız")} what={testMessage(test)} dataImpact={tx("Yeni yedekler bu depolamaya yazılamayabilir; mevcut yedekleriniz silinmedi.")} action={tx("Ayarları düzeltip bağlantıyı yeniden test edin.")} technical={[{ label: tx("Test zamanı"), value: formatDateTime(test.testedAt, tx('Henüz bildirilmedi')) }]} />
  if (health.key === 'untested') return <Notice tone="info" title={tx("Bağlantı henüz test edilmedi")}>{tx("Çalıştığından emin olmak için “Test et” düğmesini kullanın.")}</Notice>
  if (health.key === 'agent-side' && test) return <Notice tone="info" title={tx("Bu hedef Agent tarafında denetlenir")}>{testMessage(test)}</Notice>
  if (test) return <Notice tone="success" title={tx("Bağlantı sağlıklı")}>{tx("Son test {0} başarılı oldu.", { "0": formatRelativeTime(test.testedAt) })}</Notice>
  return null
}

/** Is space running out? One sentence for the person, then the numbers that matter. */
function CapacitySection({ connection }: { connection: ConnectionView }) {
  useLanguage()
  const { data: capacity, isPending } = useTenantConnectionCapacity(connection.id)
  const probe = useProbeTenantConnectionCapacity(connection.id)
  const awaitingAgent = capacity?.status === 'Pending'
  const notMeasurable = capacity?.status === 'NotMeasurable'

  const hasMetrics = capacity && !awaitingAgent && capacity.status !== 'CapacityUnavailable' && capacity.totalBytes != null
  const percentUsed = hasMetrics && capacity.totalBytes! > 0
    ? Math.min(100, Math.round(((capacity.usedBytes ?? 0) / capacity.totalBytes!) * 100))
    : 0
  const isCritical = capacity?.status === 'Critical'
  const isWarning = capacity?.status === 'Warning'
  const verdict = isCritical ? tx("Depolama alanı dolmak üzere. Yeni yedekler yazılamayabilir; yer açın veya başka bir depolama ekleyin.")
    : isWarning ? tx("Depolama alanı azalıyor. Yakında dolabilir; şimdiden yer açmayı veya ek depolama eklemeyi düşünün.")
      : tx("Yeterli boş alan var.")

  return (
    <Section
      title={<span className="flex flex-wrap items-center gap-2"><span>{tx("Depolama alanı")}</span><CapacityPill status={capacity?.status} /></span>}
      description={tx("Depolamada ne kadar boş yer kaldığı.")}
      actions={
        <Button
          variant="outline"
          size="sm"
          disabled={probe.isPending || awaitingAgent || notMeasurable}
          onClick={() => probe.mutate()}
        >
          {probe.isPending || awaitingAgent ? tx("Sorgulanıyor…") : tx("Kapasiteyi Sorgula")}
        </Button>
      }
    >
      {awaitingAgent && (
        <p role="status" className="text-sm text-text-muted">{tx("Kapasite sorgusu sırada; Agent yanıtı bekleniyor.")}</p>
      )}
      {notMeasurable && (
        <Notice tone="info" title={tx("Kapasite ölçülemiyor")}>
          {tx("Bu depolama türü kapasite veya kota bilgisi sunmadığı için doluluk ölçülemez.")}
        </Notice>
      )}
      {capacity?.status === 'CapacityUnavailable' && (
        <Notice tone="warning" title={tx("Kapasite bilgisi alınamadı")}>
          {tx("Depolama hedefine erişebilen aktif bir Agent veya bağlantı bulunamadı.")}
        </Notice>
      )}

      {hasMetrics ? (
        <div className="space-y-4">
          <p className="text-sm text-text-heading">{verdict}</p>
          <Facts columns={3}>
            <Fact label={tx("Boş Alan")}>
              <span className="text-lg font-semibold text-text-heading">{capacity.freeBytes != null ? formatBytes(capacity.freeBytes) : '—'}</span>
            </Fact>
          </Facts>

          <div className="space-y-1">
            <div className="flex justify-between text-xs text-text-muted">
              <span>{tx("Doluluk Oranı")}</span>
              <span>{percentUsed}%</span>
            </div>
            <ProgressBar label={tx("Doluluk Oranı")} value={percentUsed} tone={isCritical ? 'error' : isWarning ? 'warning' : 'primary'} />
          </div>

          <p className="text-xs text-text-muted">
            {tx("Son sorgulama: {0}", { "0": formatRelativeTime(capacity.reportedAt) })}
          </p>
        </div>
      ) : !awaitingAgent && !notMeasurable && capacity?.status !== 'CapacityUnavailable' ? (
        <p className="text-sm text-text-muted">
          {isPending ? tx("Kapasite bilgisi yükleniyor…") : tx("Kapasite bilgisi henüz sorgulanmadı. “Kapasiteyi Sorgula” düğmesini kullanarak güncel durumu alabilirsiniz.")}
        </p>
      ) : null}

      {probe.isError && (
        <p role="alert" className="mt-2 text-sm text-error">
          {tx("Kapasite sorgulama başarısız oldu.")}
        </p>
      )}
    </Section>
  )
}

/** Capacity numbers and history, quota scope, dates, provider internals and identifiers: everything the person does not need to read first. */
function OverviewTechnical({ connection, descriptor, total }: { connection: ConnectionView; descriptor?: ProviderDescriptor; total: number }) {
  useLanguage()
  const { data: capacity } = useTenantConnectionCapacity(connection.id)
  const measured = capacity && capacity.status !== 'Pending' && capacity.totalBytes != null
  const history = capacity?.history ?? []
  const items = [
    ...(measured ? [{ label: tx("Toplam Alan"), value: formatBytes(capacity.totalBytes!) }, ...(capacity.usedBytes != null ? [{ label: tx("Kullanılan Alan"), value: formatBytes(capacity.usedBytes) }] : [])] : []),
    ...footprintItems(connection, total),
    { label: tx("Kota kapsamı"), value: connection.quotaScope },
    { label: tx("Oluşturulma"), value: formatDateTime(connection.createdAt, tx('Henüz bildirilmedi')) },
    { label: tx("Son güncelleme"), value: formatDateTime(connection.updatedAt, tx('Henüz bildirilmedi')) },
    { label: tx("Topoloji"), value: topologyLabel(descriptor?.topology ?? null) },
    { label: tx("Yürütme konumu"), value: executionLocationLabel(descriptor?.executionLocation ?? null) },
    { label: tx("Test yöntemi"), value: descriptor ? testModeLabel(descriptor.testMode) : '—' },
    { label: tx("Bağlantı kimliği"), value: connection.id },
  ]
  return <TechnicalDetails items={items}>
    <div className="space-y-2"><p className="text-sm font-medium text-text-heading">{tx("Yetenekler")}</p><CapabilityChips capabilities={connection.capabilities} label={tx("{0} yetenekleri", { "0": connection.name })} /><CopyButton label={tx("Kopyala: {0}", { "0": tx("Yetenekler") })} value={connection.capabilities.join('\n')} /></div>
    {history.length > 0 && <div className="space-y-2">
      <p aria-hidden="true" className="text-sm font-medium text-text-heading">{tx("Kapasite geçmişi — en son {0} ölçüm", { "0": capacity?.historyLimit ?? 30 })}</p>
      <DataList caption={tx("Kapasite geçmişi — en son {0} ölçüm", { "0": capacity?.historyLimit ?? 30 })} rows={history} keyExtractor={measurement => `${measurement.reportedAt}-${history.indexOf(measurement)}`} columns={[
        { key: 'at', header: tx('Tarih'), render: measurement => formatDateTime(measurement.reportedAt, tx('Henüz bildirilmedi')) },
        { key: 'total', header: tx('Toplam Alan'), card: 'hidden', render: measurement => measurement.totalBytes == null ? '—' : formatBytes(measurement.totalBytes) },
        { key: 'used', header: tx('Kullanılan Alan'), render: measurement => measurement.usedBytes == null ? '—' : formatBytes(measurement.usedBytes) },
        { key: 'free', header: tx('Boş Alan'), render: measurement => measurement.freeBytes == null ? '—' : formatBytes(measurement.freeBytes) },
        { key: 'state', header: tx('Durum'), card: 'status', render: measurement => measurement.status === 'Critical' ? tx("Kritik") : measurement.status === 'Warning' ? tx("Uyarı") : measurement.status === 'Available' ? tx("Kullanılabilir") : tx("Kapasite bilgisi alınamadı") },
        { key: 'copy', header: tx('Ölçüm'), card: 'actions', render: measurement => <CopyButton label={tx("Kopyala: {0}", { "0": tx("Ölçüm") })} value={JSON.stringify(measurement)} /> },
      ]} />
    </div>}
  </TechnicalDetails>
}

function Overview({ connection, descriptor, total }: { connection: ConnectionView; descriptor?: ProviderDescriptor; total: number }) {
  useLanguage()
  return <div className="space-y-5">
    <HealthNotice connection={connection} />
    <CapacitySection connection={connection} />
    <OverviewTechnical connection={connection} descriptor={descriptor} total={total} />
  </div>
}

function Configuration({ connection, descriptor, ops }: { connection: ConnectionView; descriptor?: ProviderDescriptor; ops: ReturnType<typeof useTenantConnectionOps> }) {
  useLanguage()
  const summary = configurationSummary(connection, descriptor)
  return <Section title={tx("Yapılandırma")} description={tx("Gizli olmayan bağlantı ayarları.")} actions={<ConnectionActions connection={connection} descriptor={descriptor} ops={ops} show={['edit']} />}>
    {summary.length === 0 ? <p className="text-text-muted">{tx("Bu sağlayıcı ek yapılandırma gerektirmiyor.")}</p> : <Facts>{summary.map((item) => <Fact key={item.name} label={tx(item.label)}>{item.value}</Fact>)}</Facts>}
  </Section>
}

function Credentials({ connection, descriptor, ops }: { connection: ConnectionView; descriptor?: ProviderDescriptor; ops: ReturnType<typeof useTenantConnectionOps> }) {
  useLanguage()
  return <Section title={tx("Kimlik bilgisi")} description={tx("Değerler yalnızca sunucuya gönderilir; kaydedildikten sonra bir daha gösterilmez.")}>
    <div className="space-y-4">
      <p><SecretStatus connection={connection} /></p>
      <ConnectionActions connection={connection} descriptor={descriptor} ops={ops} show={['credentials']} />
    </div>
  </Section>
}

function Manage({ connection, descriptor, ops }: { connection: ConnectionView; descriptor?: ProviderDescriptor; ops: ReturnType<typeof useTenantConnectionOps> }) {
  useLanguage()
  const on = connection.enabled
  return <div className="space-y-5">
    <Section title={tx("Kullanılabilirlik")} description={connection.effectiveEnabled ? tx('Bu hedef yeni backup\'lar için kullanılabilir.') : tx('Bu hedef şu an devre dışı.')}>
      <div className="space-y-4">
        <p className="text-sm text-text-muted">{tx("Sahip tercihi: {0}", { "0": on ? tx('Etkin') : tx('Devre dışı') })}</p>
        {connection.administrativelyDisabled && <Notice tone="warning" title={tx("Platform kilidi")}>{tx(platformLockState)}</Notice>}
        {on ? <ul className="list-inside list-disc space-y-1 text-sm text-text-muted">{disableEffects.map((effect) => <li key={effect}>{tx(effect)}</li>)}</ul> : <p className="text-sm text-text-muted">{tx(enableEffect)}</p>}
        <ConnectionActions connection={connection} descriptor={descriptor} ops={ops} show={['toggle']} />
      </div>
    </Section>
    {connection.permissions.canDelete && <Section tone="danger" title={tx("Bağlantıyı sil")} description={tx("Bağlantı kalıcı olarak silinir. Replica veya Backup Job hâlâ bu bağlantıyı kullanıyorsa silinemez; önce bunların başka bir hedefe taşınması gerekir.")}>
      <ConnectionActions connection={connection} descriptor={descriptor} ops={ops} show={['delete']} />
    </Section>}
  </div>
}

export function ConnectionDetail() {
  useLanguage()
  const { connectionId } = useParams()
  const { tenantId } = useTenant()
  const navigate = useNavigate()
  const [params, setParams] = useSearchParams()
  const connections = useTenantConnections()
  const providers = useTenantProviders()
  const ops = useTenantConnectionOps()
  const list = connections.data?.items ?? []
  const connection = list.find((item) => item.id === connectionId)
  const descriptor = providers.data?.items.find((item) => item.providerType === connection?.providerType)
  const total = list.reduce((sum, item) => sum + item.storedBytes, 0)
  const storage = `/tenants/${tenantId}/storage`
  const removed = ops.remove.isSuccess && ops.remove.variables?.id === connectionId
  useEffect(() => { if (removed) navigate(storage, { replace: true }) }, [removed, navigate, storage])

  const hasCredentials = Boolean(connection && (connection.providerType === 'google-drive' || connection.hasSecret || connection.secretFieldNames.length > 0 || (descriptor?.secretFields.length ?? 0) > 0))
  const visibleTabs = tabs.filter((tab) => tab.value !== 'credentials' || hasCredentials)
  const requested = params.get('tab')
  const active: TabValue = visibleTabs.find((tab) => tab.value === requested)?.value ?? 'overview'
  const crumbs = [{ label: tx('Storage'), to: storage }, { label: tx('Bağlantılar'), to: storage }, { label: connection?.name ?? tx('Bağlantı') }]

  if (connections.isPending || connections.error) return <><PageHeader title={tx("Depolama bağlantısı")} crumbs={crumbs} /><QueryState isPending={connections.isPending} error={connections.error} /></>
  if (!connection) return <><PageHeader title={tx("Bağlantı bulunamadı")} crumbs={crumbs} /><Notice tone="warning" title={tx("Bu bağlantı yok veya bu tenant altında erişilemiyor")} action={<Link to={storage} className={buttonVariants({ variant: 'outline', size: 'sm' })}>{tx("Bağlantılara dön")}</Link>} /></>

  return <>
    <PageHeader crumbs={crumbs} title={connection.name} description={tx(connection.providerDisplayName)}
      meta={<><HealthBadge connection={connection} /><StatusPill domain="ownership" status={connection.ownership} />{connection.isDefault && <Badge tone="accent">{tx("Varsayılan")}</Badge>}</>}
      actions={<ConnectionActions connection={connection} descriptor={descriptor} ops={ops} show={['test']} />} />
    <Tabs idPrefix="connection" label={tx("Bağlantı bölümleri")} value={active} items={visibleTabs} onChange={(value) => setParams(value === 'overview' ? {} : { tab: value }, { replace: true })} />
    <div role="tabpanel" id={`connection-panel-${active}`} aria-labelledby={`connection-tab-${active}`} className="space-y-5">
      {active === 'overview' && <Overview connection={connection} descriptor={descriptor} total={total} />}
      {active === 'configuration' && <Configuration connection={connection} descriptor={descriptor} ops={ops} />}
      {active === 'credentials' && <Credentials connection={connection} descriptor={descriptor} ops={ops} />}
      {active === 'manage' && <Manage connection={connection} descriptor={descriptor} ops={ops} />}
    </div>
  </>
}

