import { tx, useLanguage } from '@/lib/i18n'
import { Link } from 'react-router-dom'
import { DataTable, type Column } from '@/components/data/data-table'
import { Badge } from '@/components/ui/badge'
import { platformPaths } from '@/features/platform/paths'
import { linkClass } from '@/features/platform/styles'
import { ConnectionActions } from './connection-actions'
import { ConnectionState, Footprint, LastTest, SecretStatus } from './connection-parts'
import type { ConnectionOps } from './hooks'
import type { ConnectionView, ProviderDescriptor } from './types'
import { StatusPill } from '@/components/data/status-pill'

/** One table for the platform list and the tenant-detail section; the tenant column is dropped where the tenant is the page. */
export function PlatformConnectionTable({ rows, providers, ops, showTenant, emptyMessage }: {
  rows: readonly ConnectionView[]; providers: readonly ProviderDescriptor[]; ops: ConnectionOps; showTenant: boolean; emptyMessage: string
}) {
  useLanguage()
  const columns: Column<ConnectionView>[] = [
    ...(showTenant ? [{ key: 'tenant', header: tx('Tenant'), wrap: true, render: (row: ConnectionView) => <Link className={linkClass} to={platformPaths.tenant(row.tenantId)}>{row.tenantName ?? 'Tenant'}</Link> }] : []),
    { key: 'name', header: tx('Bağlantı'), wrap: true, render: (row) => <div className="space-y-1">
      <div className="flex flex-wrap items-center gap-1.5"><span className="font-medium text-text-heading">{row.name}</span>{row.isDefault && <Badge tone="accent">{tx("Varsayılan")}</Badge>}</div>
      <p className="text-sm text-text-muted">{row.providerDisplayName}</p>
      <StatusPill domain="ownership" status={row.ownership} />
      <details className="text-xs text-text-muted"><summary className="cursor-pointer">{tx("Bağlantı kimliği")}</summary><code className="break-all">{row.id}</code></details>
    </div> },
    { key: 'enabled', header: tx('Durum'), wrap: true, render: (row) => <div className="space-y-2"><ConnectionState connection={row} /><LastTest test={row.lastTest} /></div> },
    { key: 'secret', header: tx('Kimlik bilgisi'), wrap: true, render: (row) => <SecretStatus connection={row} /> },
    { key: 'footprint', header: tx('Replica / Boyut'), render: (row) => <div className="space-y-2"><Footprint connection={row} /><p className="text-xs text-text-muted">{tx("Kota kapsamı:")}{' '}<code className="text-text">{row.quotaScope}</code></p></div> },
    { key: 'actions', header: tx('İşlemler'), wrap: true, render: (row) => <ConnectionActions connection={row} descriptor={providers.find((provider) => provider.providerType === row.providerType)} ops={ops} /> },
  ]
  return <DataTable rows={rows} columns={columns} keyExtractor={(row) => row.id} emptyMessage={emptyMessage} />
}
