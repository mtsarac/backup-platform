import { Link } from 'react-router-dom'
import { DataTable, type Column } from '@/components/data/data-table'
import { Badge } from '@/components/ui/badge'
import { platformPaths } from '@/features/platform/paths'
import { linkClass } from '@/features/platform/styles'
import { ConnectionActions } from './connection-actions'
import { ConnectionState, Footprint, LastTest, OwnershipBadge, SecretStatus } from './connection-parts'
import type { ConnectionOps } from './hooks'
import type { ConnectionView, ProviderDescriptor } from './types'

/** One table for the platform list and the tenant-detail section; the tenant column is dropped where the tenant is the page. */
export function PlatformConnectionTable({ rows, providers, ops, showTenant, emptyMessage }: {
  rows: readonly ConnectionView[]; providers: readonly ProviderDescriptor[]; ops: ConnectionOps; showTenant: boolean; emptyMessage: string
}) {
  const columns: Column<ConnectionView>[] = [
    ...(showTenant ? [{ key: 'tenant', header: 'Tenant', wrap: true, render: (row: ConnectionView) => <Link className={linkClass} to={platformPaths.tenant(row.tenantId)}>{row.tenantName ?? 'Tenant'}</Link> }] : []),
    { key: 'name', header: 'Ad', wrap: true, render: (row) => <div>
      <div className="flex flex-wrap items-center gap-1.5"><span className="font-medium text-text-heading">{row.name}</span>{row.isDefault && <Badge tone="accent">Varsayılan</Badge>}</div>
      <code className="block break-all text-[11px] text-text-muted" title="Bağlantı kimliği (Kota Politikası storageConnection seçicisinde kullanılır)">{row.id}</code>
    </div> },
    { key: 'provider', header: 'Storage Provider', wrap: true, render: (row) => <div><div>{row.providerDisplayName}</div><code className="text-xs text-text-muted">{row.providerType}</code></div> },
    { key: 'ownership', header: 'Sahiplik', render: (row) => <OwnershipBadge ownership={row.ownership} /> },
    { key: 'quotaScope', header: 'Kota kapsamı', render: (row) => <code>{row.quotaScope}</code> },
    { key: 'enabled', header: 'Durum', render: (row) => <ConnectionState connection={row} /> },
    { key: 'secret', header: 'Kimlik bilgisi', wrap: true, render: (row) => <SecretStatus connection={row} /> },
    { key: 'lastTest', header: 'Son test', wrap: true, render: (row) => <LastTest test={row.lastTest} /> },
    { key: 'footprint', header: 'Replica / Boyut', render: (row) => <Footprint connection={row} /> },
    { key: 'actions', header: 'İşlemler', wrap: true, render: (row) => <ConnectionActions connection={row} descriptor={providers.find((provider) => provider.providerType === row.providerType)} ops={ops} /> },
  ]
  return <DataTable rows={rows} columns={columns} keyExtractor={(row) => row.id} emptyMessage={emptyMessage} />
}
