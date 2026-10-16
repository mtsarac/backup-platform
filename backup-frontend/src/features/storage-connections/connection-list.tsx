import { currentLocale, tx, useLanguage } from '@/lib/i18n'
import { Link } from 'react-router-dom'
import { ArrowRight, Cable } from 'lucide-react'
import { QueryState } from '@/components/data/query-state'
import { KpiCard } from '@/components/data/kpi-card'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { buttonVariants } from '@/components/ui/button-variants'
import { EmptyState } from '@/components/ui/empty-state'
import { Section } from '@/components/ui/section'
import { agentDate } from '@/features/agents/format'
import { formatCapacity, formatExactBytes } from '@/features/storage/format'
import { useTenant } from '@/features/tenancy/use-tenant'
import { HealthBadge, OwnershipBadge } from './connection-parts'
import { useTenantConnectionOps, useTenantConnections } from './hooks'
import { connectionHealth, connectionErrorMessage } from './labels'
import { ProviderIcon } from './provider-icon'
import { connectionPath } from '@/lib/routes'
import type { ConnectionView } from './types'

function ConnectionRow({ connection, share, tenantId, onTest, testing, testError }: { connection: ConnectionView; share: number; tenantId: string; onTest: () => void; testing: boolean; testError?: string }) {
  useLanguage()
  return <li><article aria-label={connection.name} className="grid items-center gap-x-6 gap-y-3 px-5 py-4 md:grid-cols-[minmax(0,2fr)_minmax(0,1.3fr)_minmax(0,1.2fr)_11rem]">
    <div className="flex min-w-0 items-center gap-3">
      <ProviderIcon providerType={connection.providerType} />
      <div className="min-w-0">
        <h3 className="flex flex-wrap items-center gap-x-2 gap-y-1 font-medium text-text-heading">
          <Link to={connectionPath(tenantId, connection.id)} className="break-words underline-offset-4 hover:underline">{connection.name}</Link>
          {connection.isDefault && <Badge tone="accent">{tx("Varsayılan")}</Badge>}
        </h3>
        <p className="mt-0.5 flex flex-wrap items-center gap-x-2 text-sm text-text-muted">{connection.providerDisplayName}<OwnershipBadge ownership={connection.ownership} /></p>
      </div>
    </div>
    <div className="min-w-0 space-y-1">
      <HealthBadge connection={connection} />
      <p className="text-sm text-text-muted">{connection.lastTest ? tx("Son test: {0}", { "0": agentDate(connection.lastTest.testedAt) }) : tx('Henüz test edilmedi')}</p>
    </div>
    <div className="min-w-0 space-y-1.5">
      <p className="text-sm"><span title={formatExactBytes(connection.storedBytes)} className="font-medium text-text-heading">{formatCapacity(connection.storedBytes)}</span> <span className="text-text-muted">· {connection.replicaCount.toLocaleString(currentLocale())}{' '}{tx("Replica")}</span></p>
      <div aria-hidden="true" className="h-1.5 overflow-hidden rounded-full bg-surface-3"><div className="h-full rounded-full bg-primary" style={{ width: `${share}%` }} /></div>
    </div>
    <div className="flex flex-wrap items-center gap-2 md:justify-end">
      {connection.permissions.canTest && <Button variant="outline" size="sm" aria-label={tx("{0} bağlantısı: test et", { "0": connection.name })} disabled={testing} onClick={onTest}>{testing ? tx('Test ediliyor…') : tx('Test et')}</Button>}
      <Link to={connectionPath(tenantId, connection.id)} aria-label={tx("{0} bağlantısını yönet", { "0": connection.name })} className={buttonVariants({ variant: 'ghost', size: 'sm' })}>{tx("Yönet")}<ArrowRight aria-hidden="true" className="size-4" /></Link>
    </div>
    {testError && <p role="alert" className="text-sm text-error md:col-span-4">{testError}</p>}
  </article></li>
}

/** The tenant's destinations at a glance: how many work, which need attention, how much data each holds. Details and changes live on the connection's own page. */
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
    <QueryState isPending={connections.isPending} error={connections.error}>
      <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
        <KpiCard label={tx("Bağlantı")} value={list.length.toLocaleString(currentLocale())} />
        <KpiCard label={tx("Sağlıklı")} value={healthy.toLocaleString(currentLocale())} tone="success" />
        <KpiCard label={tx("Dikkat gerektiren")} value={attention.toLocaleString(currentLocale())} tone={attention > 0 ? 'error' : undefined} />
        <KpiCard label={tx("Saklanan veri")} value={<span title={formatExactBytes(total)}>{formatCapacity(total)}</span>} />
      </div>
      <Section title={tx("Depolama hedefleri")} description={tx("Backup'ların yazıldığı yerler. Bir hedefi devre dışı bırakmak yalnızca yeni backup'ları durdurur; mevcut backup'lar silinmez.")} flush>
        {list.length === 0
          ? <EmptyState icon={Cable} title={tx("Henüz depolama bağlantısı yok")} action={canAdmin ? <Link to={`/tenants/${tenantId}/storage/connections/new`} className={buttonVariants()}>{tx("İlk bağlantıyı ekle")}</Link> : undefined}>{tx("Backup'ların yazılacağı bir hedef ekleyin; sihirbaz bağlantıyı kurar ve test eder.")}</EmptyState>
          : <ul className="divide-y divide-border">{list.map((connection) => <ConnectionRow key={connection.id} connection={connection} tenantId={tenantId} share={total > 0 ? Math.round((connection.storedBytes / total) * 100) : 0}
            testing={ops.test.isPending && ops.test.variables?.id === connection.id} testError={ops.test.error && ops.test.variables?.id === connection.id ? connectionErrorMessage(ops.test.error) : undefined} onTest={() => ops.test.mutate(connection)} />)}</ul>}
      </Section>
    </QueryState>
  </div>
}
