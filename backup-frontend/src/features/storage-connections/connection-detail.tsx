import { useEffect } from 'react'
import { Link, useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { QueryState } from '@/components/data/query-state'
import { Badge } from '@/components/ui/badge'
import { buttonVariants } from '@/components/ui/button-variants'
import { Notice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { Fact, Facts, Section } from '@/components/ui/section'
import { Tabs } from '@/components/ui/tabs'
import { agentDate } from '@/features/agents/format'
import { formatCapacity, formatExactBytes } from '@/features/storage/format'
import { useTenant } from '@/features/tenancy/use-tenant'
import { ConnectionActions } from './connection-actions'
import { CapabilityChips, HealthBadge, OwnershipBadge, SecretStatus } from './connection-parts'
import { useTenantConnectionOps, useTenantConnections, useTenantProviders } from './hooks'
import { configurationSummary, connectionHealth, disableEffects, enableEffect, executionLocationLabel, testMessage, testModeLabel, topologyLabel } from './labels'
import type { ConnectionView, ProviderDescriptor } from './types'

const tabs = [
  { value: 'overview', label: 'Genel bakış' },
  { value: 'configuration', label: 'Yapılandırma' },
  { value: 'credentials', label: 'Kimlik bilgisi' },
  { value: 'manage', label: 'Yönetim' },
] as const
type TabValue = (typeof tabs)[number]['value']

/** The headline finding for the connection, worded for what the person should do next. */
function HealthNotice({ connection }: { connection: ConnectionView }) {
  const health = connectionHealth(connection)
  const test = connection.lastTest
  if (health.key === 'disabled') return <Notice tone="warning" title="Bu hedef devre dışı">Yeni backup başlatılamaz. Mevcut backup'lar indirilebilir ve geri yüklenebilir kalır.</Notice>
  if (health.key === 'reauthorize') return <Notice tone="warning" title="Google hesabının yeniden yetkilendirilmesi gerekiyor">Kimlik bilgisi sekmesinden hesabı yeniden bağlayın; bağlanana kadar bu hedefe erişen işlemler başarısız olabilir.</Notice>
  if (health.key === 'failed' && test) return <Notice tone="error" title="Son test başarısız">{testMessage(test)} <span className="text-text-muted">({agentDate(test.testedAt)})</span></Notice>
  if (health.key === 'untested') return <Notice tone="info" title="Bağlantı henüz test edilmedi">Çalıştığından emin olmak için “Test et” düğmesini kullanın.</Notice>
  if (health.key === 'agent-side' && test) return <Notice tone="info" title="Bu hedef Agent tarafında denetlenir">{testMessage(test)}</Notice>
  if (test) return <Notice tone="success" title="Bağlantı sağlıklı">Son test {agentDate(test.testedAt)} tarihinde başarılı oldu.</Notice>
  return null
}

function Overview({ connection, descriptor, total }: { connection: ConnectionView; descriptor?: ProviderDescriptor; total: number }) {
  const share = total > 0 ? Math.round((connection.storedBytes / total) * 100) : 0
  return <div className="space-y-5">
    <HealthNotice connection={connection} />
    <Section title="Depolama ve kullanım">
      <Facts columns={3}>
        <Fact label="Saklanan veri"><span title={formatExactBytes(connection.storedBytes)} className="text-lg font-semibold text-text-heading">{formatCapacity(connection.storedBytes)}</span></Fact>
        <Fact label="Replica"><span className="text-lg font-semibold text-text-heading">{connection.replicaCount.toLocaleString('tr-TR')}</span></Fact>
        <Fact label="Tenant'taki payı">%{share}</Fact>
        <Fact label="Kota kapsamı"><code>{connection.quotaScope}</code></Fact>
        <Fact label="Oluşturulma">{agentDate(connection.createdAt)}</Fact>
        <Fact label="Son güncelleme">{agentDate(connection.updatedAt)}</Fact>
      </Facts>
    </Section>
    <Section title="Sağlayıcı" description={connection.providerDisplayName}>
      <Facts columns={3}>
        <Fact label="Topoloji">{topologyLabel(descriptor?.topology ?? null)}</Fact>
        <Fact label="Yürütme konumu">{executionLocationLabel(descriptor?.executionLocation ?? null)}</Fact>
        <Fact label="Test yöntemi">{descriptor ? testModeLabel(descriptor.testMode) : '—'}</Fact>
      </Facts>
      <div className="mt-4"><CapabilityChips capabilities={connection.capabilities} label={`${connection.name} yetenekleri`} /></div>
    </Section>
  </div>
}

function Configuration({ connection, descriptor, ops }: { connection: ConnectionView; descriptor?: ProviderDescriptor; ops: ReturnType<typeof useTenantConnectionOps> }) {
  const summary = configurationSummary(connection, descriptor)
  return <Section title="Yapılandırma" description="Gizli olmayan bağlantı ayarları." actions={<ConnectionActions connection={connection} descriptor={descriptor} ops={ops} show={['edit']} />}>
    {summary.length === 0 ? <p className="text-text-muted">Bu sağlayıcı ek yapılandırma gerektirmiyor.</p> : <Facts>{summary.map((item) => <Fact key={item.name} label={item.label}>{item.value}</Fact>)}</Facts>}
  </Section>
}

function Credentials({ connection, descriptor, ops }: { connection: ConnectionView; descriptor?: ProviderDescriptor; ops: ReturnType<typeof useTenantConnectionOps> }) {
  return <Section title="Kimlik bilgisi" description="Değerler yalnızca sunucuya gönderilir; kaydedildikten sonra bir daha gösterilmez.">
    <div className="space-y-4">
      <p><SecretStatus connection={connection} /></p>
      <ConnectionActions connection={connection} descriptor={descriptor} ops={ops} show={['credentials']} />
    </div>
  </Section>
}

function Manage({ connection, descriptor, ops }: { connection: ConnectionView; descriptor?: ProviderDescriptor; ops: ReturnType<typeof useTenantConnectionOps> }) {
  const on = connection.enabled
  return <div className="space-y-5">
    <Section title="Kullanılabilirlik" description={connection.effectiveEnabled ? 'Bu hedef yeni backup\'lar için kullanılabilir.' : 'Bu hedef şu an devre dışı.'}>
      <div className="space-y-4">
        <p className="text-sm text-text-muted">Sahip tercihi: {on ? 'Etkin' : 'Devre dışı'}</p>
        {connection.administrativelyDisabled && <Notice tone="warning" title="Platform kilidi">Platform kilidi kaldırılmadan bu hedef yeni backup'lar için kullanılamaz.</Notice>}
        {on ? <ul className="list-inside list-disc space-y-1 text-sm text-text-muted">{disableEffects.map((effect) => <li key={effect}>{effect}</li>)}</ul> : <p className="text-sm text-text-muted">{enableEffect}</p>}
        <ConnectionActions connection={connection} descriptor={descriptor} ops={ops} show={['toggle']} />
      </div>
    </Section>
    {connection.permissions.canDelete && <Section tone="danger" title="Bağlantıyı sil" description="Bağlantı kalıcı olarak silinir. Replica veya Backup Job hâlâ bu bağlantıyı kullanıyorsa silinemez; önce bunların başka bir hedefe taşınması gerekir.">
      <ConnectionActions connection={connection} descriptor={descriptor} ops={ops} show={['delete']} />
    </Section>}
  </div>
}

export function ConnectionDetail() {
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
  const crumbs = [{ label: 'Storage', to: storage }, { label: 'Bağlantılar', to: storage }, { label: connection?.name ?? 'Bağlantı' }]

  if (connections.isPending || connections.error) return <><PageHeader title="Depolama bağlantısı" crumbs={crumbs} /><QueryState isPending={connections.isPending} error={connections.error} /></>
  if (!connection) return <><PageHeader title="Bağlantı bulunamadı" crumbs={crumbs} /><Notice tone="warning" title="Bu bağlantı yok veya bu tenant altında erişilemiyor" action={<Link to={storage} className={buttonVariants({ variant: 'outline', size: 'sm' })}>Bağlantılara dön</Link>} /></>

  return <>
    <PageHeader crumbs={crumbs} title={connection.name} description={connection.providerDisplayName}
      meta={<><HealthBadge connection={connection} /><OwnershipBadge ownership={connection.ownership} />{connection.isDefault && <Badge tone="accent">Varsayılan</Badge>}</>}
      actions={<ConnectionActions connection={connection} descriptor={descriptor} ops={ops} show={['test']} />} />
    <Tabs idPrefix="connection" label="Bağlantı bölümleri" value={active} items={visibleTabs} onChange={(value) => setParams(value === 'overview' ? {} : { tab: value }, { replace: true })} />
    <div role="tabpanel" id={`connection-panel-${active}`} aria-labelledby={`connection-tab-${active}`} className="space-y-5">
      {active === 'overview' && <Overview connection={connection} descriptor={descriptor} total={total} />}
      {active === 'configuration' && <Configuration connection={connection} descriptor={descriptor} ops={ops} />}
      {active === 'credentials' && <Credentials connection={connection} descriptor={descriptor} ops={ops} />}
      {active === 'manage' && <Manage connection={connection} descriptor={descriptor} ops={ops} />}
    </div>
  </>
}

