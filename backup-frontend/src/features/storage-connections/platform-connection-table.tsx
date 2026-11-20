import { tx, useLanguage, currentLocale } from '@/lib/i18n'
import { Link } from 'react-router-dom'
import { DataList, type Column, type SortState } from '@/components/data/data-list'
import { StatusPill } from '@/components/data/status-pill'
import { RelativeTime } from '@/components/data/relative-time'
import { Badge } from '@/components/ui/badge'
import { TechnicalDetails } from '@/components/ui/technical-details'
import { platformPaths } from '@/features/platform/paths'
import { linkClass } from '@/components/ui/form-styles'
import { ConnectionActions } from './connection-actions'
import { connectionDisplayName } from './labels'
import { ConnectionState, LastTest, SecretStatus } from './connection-parts'
import type { ConnectionOps } from './hooks'
import type { ConnectionView, ProviderDescriptor } from './types'
import { formatBytes } from '@/lib/format'

/**
 * One list for the platform page and the tenant-detail section (cards on phones); the tenant column is dropped where the tenant is the page.
 * Visible: name, workspace, state, provider, ownership, last test, size, created, actions. Collapsed: connection id, quota scope, replica count and exact size.
 */
export function PlatformConnectionTable({ rows, providers, ops, showTenant, emptyMessage, sort }: {
  rows: readonly ConnectionView[]; providers: readonly ProviderDescriptor[]; ops: ConnectionOps; showTenant: boolean; emptyMessage: string; sort?: SortState
}) {
  useLanguage()
  const columns: Column<ConnectionView>[] = [
    { key: 'name', card: 'title', header: tx('Bağlantı'), sortable: true, wrap: true, render: (row) => <div className="flex flex-wrap items-center gap-1.5"><span className="font-medium text-text-heading">{connectionDisplayName(row)}</span>{row.isDefault && <Badge tone="accent">{tx("Varsayılan")}</Badge>}</div> },
    { key: 'enabled', card: 'status', header: tx('Durum'), sortable: true, wrap: true, render: (row) => <ConnectionState connection={row} /> },
    ...(showTenant ? [{ key: 'tenant', header: tx('Çalışma alanı'), sortable: true, wrap: true, render: (row: ConnectionView) => <Link className={linkClass} to={platformPaths.tenant(row.tenantId)}>{row.tenantName ?? tx('Çalışma alanı')}</Link> }] : []),
    { key: 'provider', header: tx('Depolama sağlayıcısı'), sortable: true, wrap: true, render: (row) => tx(row.providerDisplayName) },
    { key: 'ownership', header: tx('Sahiplik'), sortable: true, render: (row) => <StatusPill domain="ownership" status={row.ownership} /> },
    { key: 'lastTest', header: tx('Son test'), wrap: true, render: (row) => <LastTest test={row.lastTest} /> },
    { key: 'size', header: tx('Saklanan veri'), render: (row) => <span title={row.storedBytes.toLocaleString(currentLocale())}>{formatBytes(row.storedBytes)}</span> },
    { key: 'createdAt', header: tx('Oluşturulma'), sortable: true, render: (row) => <RelativeTime value={row.createdAt} /> },
    { key: 'secret', card: 'hidden', header: tx('Kimlik bilgisi'), wrap: true, render: (row) => <SecretStatus connection={row} /> },
    { key: 'actions', card: 'actions', header: tx('İşlemler'), wrap: true, render: (row) => <div className="space-y-2">
      <ConnectionActions connection={row} descriptor={providers.find((provider) => provider.providerType === row.providerType)} ops={ops} />
      <TechnicalDetails items={[
        { label: tx("Bağlantı kimliği"), value: row.id },
        { label: tx("Kota kapsamı"), value: row.quotaScope },
        { label: tx("Replica"), value: `${row.replicaCount.toLocaleString(currentLocale())} ${tx("Replica")}` },
        { label: tx("Tam boyut (bayt)"), value: row.storedBytes.toLocaleString(currentLocale()) },
      ]} />
    </div> },
  ]
  return <DataList caption={tx("Depolama hedefleri")} rows={rows} columns={columns} sort={sort} keyExtractor={(row) => row.id} emptyMessage={emptyMessage} />
}
