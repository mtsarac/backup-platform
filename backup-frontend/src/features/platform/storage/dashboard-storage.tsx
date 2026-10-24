import { tx, useLanguage } from '@/lib/i18n'
import { Link } from 'react-router-dom'
import { DataTable, type Column } from '@/components/data/data-table'
import { Section } from '@/components/ui/section'
import type { StorageUsageRow } from '@/features/storage/types'
import { bareTableClass } from '../list-empty'
import { platformPaths } from '../paths'
import { PlatformQueryState } from '../platform-query-state'
import { linkClass } from '../styles'
import { useStorageUsage } from './hooks'
import { formatBytes } from '@/lib/format'
import { StatusPill } from '@/components/data/status-pill'

const columns: Column<StorageUsageRow>[] = [
  { key: 'tenant', get header() { return tx("Tenant") }, wrap: true, render: (row) => <Link className={linkClass} to={platformPaths.tenant(row.tenantId)}>{row.tenantName}</Link> },
  { key: 'used', get header() { return tx("Kullanılan") }, render: (row) => row.hasUsage && row.usedBytes !== null ? formatBytes(row.usedBytes) : tx('Henüz hesaplanmadı') },
  { key: 'reserved', get header() { return tx("Rezerve") }, render: (row) => row.hasUsage && row.reservedBytes !== null ? formatBytes(row.reservedBytes) : '—' },
  { key: 'limit', get header() { return tx("Limit") }, render: (row) => row.limitBytes === null ? row.subscriptionStatus === 'NoSubscription' ? tx('Limit tanımlı değil') : 'Limitsiz' : formatBytes(row.limitBytes) },
  { key: 'state', get header() { return tx("Kota durumu") }, render: (row) => row.state ? <StatusPill domain="quota" status={row.state} /> : tx('Henüz hesaplanmadı') },
]

/** A bounded preview, never a platform total calculated from one page. */
export function DashboardStorage() {
  useLanguage()
  const query = useStorageUsage({ page: 1, pageSize: 5 })
  return <Section title={tx("Depolama kullanımı")} description={query.data ? tx("{0} tenant · İlk {1} kayıt gösteriliyor; tüm kullanım için listeyi açın.", { "0": query.data.totalCount, "1": query.data.items.length }) : tx('Kullanılan ve rezerve alan, etkin limit ve kota durumu.')} flush className="overflow-hidden"
    actions={<Link className={`${linkClass} text-sm`} to={platformPaths.storage}>{tx("Tüm depolama kullanımı")}</Link>}>
    <PlatformQueryState isPending={query.isPending} error={query.error}>
      {query.data && <DataTable className={bareTableClass} rows={query.data.items} columns={columns} keyExtractor={(row) => row.tenantId} emptyMessage={tx("Henüz depolama kullanım kaydı yok.")} />}
    </PlatformQueryState>
  </Section>
}
