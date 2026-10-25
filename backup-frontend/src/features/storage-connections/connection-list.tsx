import { currentLocale, tx, useLanguage } from '@/lib/i18n'
import { Link } from 'react-router-dom'
import { ArrowRight, Cable } from 'lucide-react'
import { QueryState } from '@/components/data/query-state'
import { KpiCard } from '@/components/data/kpi-card'
import { RelativeTime } from '@/components/data/relative-time'
import { StatusPill } from '@/components/data/status-pill'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { buttonVariants } from '@/components/ui/button-variants'
import { EmptyState } from '@/components/ui/empty-state'
import { Section } from '@/components/ui/section'
import { TechnicalDetails } from '@/components/ui/technical-details'
import { useTenant } from '@/features/tenancy/use-tenant'
import { CapacityPill, HealthBadge } from './connection-parts'
import { useTenantConnectionCapacity, useTenantConnectionOps, useTenantConnections } from './hooks'
import { connectionHealth, footprintItems, platformLockState } from './labels'
import { ProviderIcon } from './provider-icon'
import { connectionPath } from '@/lib/routes'
import type { ConnectionView } from './types'
import { formatBytes } from '@/lib/format'
import { errorMessage } from '@/lib/api/failure-messages'

function ConnectionCard({ connection, total, tenantId, onTest, testing, testError }: { connection: ConnectionView; total: number; tenantId: string; onTest: () => void; testing: boolean; testError?: string }) {
  useLanguage()
  const { data: capacity } = useTenantConnectionCapacity(connection.id)
  const path = connectionPath(tenantId, connection.id)

  return <li><article aria-label={connection.name} className="space-y-3 p-5">
    <div className="flex flex-wrap items-start justify-between gap-3">
      <div className="flex min-w-0 items-center gap-3">
        <ProviderIcon providerType={connection.providerType} />
        <h3 className="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-1 font-medium text-text-heading">
          <Link to={path} className="break-words underline-offset-4 hover:underline">{connection.name}</Link>
          {connection.isDefault && <Badge tone="accent">{tx("Varsayılan")}</Badge>}
        </h3>
      </div>
      <div className="flex flex-wrap items-center gap-2">
        <HealthBadge connection={connection} />
        {connection.administrativelyDisabled && <StatusPill level="attention">{tx("Platform kilidi")}</StatusPill>}
        <CapacityPill status={capacity?.status} />
        {capacity?.status === 'Pending' && <span role="status" className="text-xs text-text-muted">{tx("Agent yanıtı bekleniyor")}</span>}
      </div>
    </div>
    <dl className="grid gap-x-6 gap-y-2 sm:grid-cols-2">
      <div className="min-w-0"><dt className="text-sm text-text-muted">{tx("Sağlayıcı")}</dt><dd className="mt-0.5 flex flex-wrap items-center gap-x-2 text-[0.9375rem] text-text">{tx(connection.providerDisplayName)}<StatusPill domain="ownership" status={connection.ownership} /></dd></div>
      <div className="min-w-0"><dt className="text-sm text-text-muted">{tx("Son test")}</dt><dd className="mt-0.5 text-[0.9375rem] text-text">{connection.lastTest ? <RelativeTime value={connection.lastTest.testedAt} /> : tx('Henüz test edilmedi')}</dd></div>
    </dl>
    {connection.administrativelyDisabled && <p className="text-sm text-text-muted">{tx(platformLockState)}</p>}
    <div className="flex flex-wrap items-center gap-2">
      {connection.permissions.canTest && <Button variant="outline" size="sm" aria-label={tx("{0} bağlantısı: test et", { "0": connection.name })} disabled={testing} onClick={onTest}>{testing ? tx('Test ediliyor…') : tx('Test et')}</Button>}
      <Link to={path} aria-label={tx("{0} bağlantısını yönet", { "0": connection.name })} className={buttonVariants({ variant: 'ghost', size: 'sm' })}>{tx("Yönet")}<ArrowRight aria-hidden="true" className="size-4" /></Link>
    </div>
    {testError && <p role="alert" className="text-sm text-error">{testError}</p>}
    <TechnicalDetails items={[...footprintItems(connection, total), { label: tx("Bağlantı kimliği"), value: connection.id }]} />
  </article></li>
}

/** The tenant's destinations as cards: name, health, last test, provider. Statistics are collapsed; changes live on the connection's own page. */
export function ConnectionList() {
  useLanguage()
  const { tenantId, canAdmin } = useTenant()
  const connections = useTenantConnections()
  const ops = useTenantConnectionOps()
  const list = connections.data?.items ?? []
  const total = list.reduce((sum, item) => sum + item.storedBytes, 0)
  const healths = list.map((item) => connectionHealth(item).key)
  const healthy = healths.filter((key) => key === 'healthy').length
  const attention = healths.filter((key) => key === 'failed' || key === 'reauthorize').length
  return <div className="space-y-5">
    <QueryState isPending={connections.isPending} error={connections.error} onRetry={() => void connections.refetch()}>
      <Section title={tx("Depolama hedefleri")} description={tx("Backup'ların yazıldığı yerler. Bir hedefi devre dışı bırakmak yalnızca yeni backup'ları durdurur; mevcut backup'lar silinmez.")} flush>
        {list.length === 0
          ? <EmptyState icon={Cable} message={tx("Henüz depolama bağlantısı yok")} canAct={canAdmin} action={<Link to={`/tenants/${tenantId}/storage/connections/new`} className={buttonVariants()}>{tx("İlk bağlantıyı ekle")}</Link>} />
          : <ul className="divide-y divide-border">{list.map((connection) => <ConnectionCard key={connection.id} connection={connection} tenantId={tenantId} total={total}
            testing={ops.test.isPending && ops.test.variables?.id === connection.id} testError={ops.test.error && ops.test.variables?.id === connection.id ? errorMessage(ops.test.error, 'storageConnection') : undefined} onTest={() => ops.test.mutate(connection)} />)}</ul>}
      </Section>
      {list.length > 0 && <details className="group rounded-xl border border-border bg-surface shadow-card">
        <summary className="min-h-11 cursor-pointer select-none rounded-xl px-5 py-3 font-semibold text-text-heading">{tx("Ayrıntılar")}</summary>
        <div className="grid grid-cols-2 gap-3 border-t border-border p-5 lg:grid-cols-4">
          <KpiCard label={tx("Bağlantı")} value={list.length.toLocaleString(currentLocale())} />
          <KpiCard label={tx("Sağlıklı")} value={healthy.toLocaleString(currentLocale())} tone="success" />
          <KpiCard label={tx("Dikkat gerektiren")} value={attention.toLocaleString(currentLocale())} tone={attention > 0 ? 'error' : undefined} />
          <KpiCard label={tx("Saklanan veri")} value={formatBytes(total)} />
        </div>
      </details>}
    </QueryState>
  </div>
}
