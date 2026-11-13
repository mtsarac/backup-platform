import { tx, useLanguage } from '@/lib/i18n'
import { Link } from 'react-router-dom'
import { Cable } from 'lucide-react'
import { QueryState } from '@/components/data/query-state'
import { RelativeTime } from '@/components/data/relative-time'
import { StatusPill } from '@/components/data/status-pill'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
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
        <CapacityPill status={capacity?.status} />
        {capacity?.status === 'Pending' && <span role="status" className="text-xs text-text-muted">{tx("Agent yanıtı bekleniyor")}</span>}
      </div>
    </div>
    <dl className="grid gap-x-6 gap-y-2 sm:grid-cols-3">
      <div className="min-w-0"><dt className="text-sm text-text-muted">{tx("Sağlayıcı")}</dt><dd className="mt-0.5 flex flex-wrap items-center gap-x-2 text-body text-text">{tx(connection.providerDisplayName)}<StatusPill domain="ownership" status={connection.ownership} /></dd></div>
      {connectionHealth(connection).key !== 'untested' && <div className="min-w-0"><dt className="text-sm text-text-muted">{tx("Son test")}</dt><dd className="mt-0.5 text-body text-text">{connection.lastTest ? <RelativeTime value={connection.lastTest.testedAt} /> : tx('Henüz test edilmedi')}</dd></div>}
      {capacity && capacity.status !== 'Pending' && capacity.freeBytes != null && <div className="min-w-0"><dt className="text-sm text-text-muted">{tx("Boş Alan")}</dt><dd className="mt-0.5 text-body text-text">{formatBytes(capacity.freeBytes)}</dd></div>}
    </dl>
    {connection.administrativelyDisabled && <p className="text-sm font-medium text-warning">{tx(platformLockState)}</p>}
    {connection.permissions.canTest && <div><Button variant="outline" size="sm" aria-label={tx("{0} bağlantısı: test et", { "0": connection.name })} disabled={testing} onClick={onTest}>{testing ? tx('Test ediliyor…') : tx('Test et')}</Button></div>}
    {testError && <p role="alert" className="text-sm text-error">{testError}</p>}
    <TechnicalDetails items={[...footprintItems(connection, total), { label: tx("Bağlantı kimliği"), value: connection.id }]} />
  </article></li>
}

/** The tenant's destinations as cards: name (the link to its page), health, capacity, last test and provider. Statistics are collapsed; changes live on the connection's own page. */
export function ConnectionList() {
  useLanguage()
  const { tenantId, canAdmin } = useTenant()
  const connections = useTenantConnections()
  const ops = useTenantConnectionOps()
  const list = connections.data?.items ?? []
  const total = list.reduce((sum, item) => sum + item.storedBytes, 0)
  return <QueryState isPending={connections.isPending} error={connections.error} onRetry={() => void connections.refetch()}>
    <Section title={tx("Depolama hedefleri")} description={tx("Backup'ların yazıldığı yerler. Bir hedefi devre dışı bırakmak yalnızca yeni backup'ları durdurur; mevcut backup'lar silinmez.")} flush>
      {list.length === 0
        ? <EmptyState icon={Cable} message={canAdmin ? tx("Henüz depolama bağlantısı yok. İlk hedefi “Bağlantı ekle” ile ekleyin.") : tx("Henüz depolama bağlantısı yok")} canAct={canAdmin} />
        : <>
          <ul className="divide-y divide-border">{list.map((connection) => <ConnectionCard key={connection.id} connection={connection} tenantId={tenantId} total={total}
            testing={ops.test.isPending && ops.test.variables?.id === connection.id} testError={ops.test.error && ops.test.variables?.id === connection.id ? errorMessage(ops.test.error, 'storageConnection') : undefined} onTest={() => ops.test.mutate(connection)} />)}</ul>
        </>}
    </Section>
  </QueryState>
}
