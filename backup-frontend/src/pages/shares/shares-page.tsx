import { Share2 } from 'lucide-react'
import { Link } from 'react-router-dom'
import { DataList, type Column } from '@/components/data/data-list'
import { QueryState } from '@/components/data/query-state'
import { StatusPill } from '@/components/data/status-pill'
import { EmptyState } from '@/components/ui/empty-state'
import { PageHeader } from '@/components/ui/page-header'
import { linkClass, rowLinkClass } from '@/components/ui/form-styles'
import { useShares } from '@/features/shares/hooks'
import type { BackupShareView } from '@/features/shares/types'
import { useTenant } from '@/features/tenancy/use-tenant'
import { formatDateTime } from '@/lib/format'
import { tx, useLanguage } from '@/lib/i18n'

export function SharesPage() {
  useLanguage()
  const { tenantId } = useTenant()
  const query = useShares(tenantId)
  const shares = query.data ?? []
  const base = `/tenants/${tenantId}`

  const columns: Column<BackupShareView>[] = [
    {
      key: 'artifact',
      header: tx('Yedek dosyası'),
      card: 'title',
      render: (share) => (
        <Link className={`${linkClass} ${rowLinkClass}`} to={`${base}/artifacts/${share.artifactId}`}>
          {share.artifactName || tx('Yedek dosyası')}
        </Link>
      ),
    },
    {
      key: 'creator',
      header: tx('Oluşturan'),
      render: (share) => share.createdByDisplayName || share.createdByUserId,
    },
    {
      key: 'role',
      header: tx('Rol'),
      render: (share) => share.createdByRole,
    },
    {
      key: 'createdAt',
      header: tx('Oluşturulma'),
      render: (share) => formatDateTime(share.createdAtUtc),
    },
    {
      key: 'expiresAt',
      header: tx('Bitiş'),
      render: (share) => formatDateTime(share.expiresAtUtc),
    },
    {
      key: 'status',
      header: tx('Durum'),
      card: 'status',
      render: (share) => <StatusPill domain="share" status={share.status} />,
    },
    {
      key: 'reason',
      header: tx('Gerekçe'),
      wrap: true,
      render: (share) => share.reason || '—',
    },
  ]

  return (
    <div className="space-y-6">
      <PageHeader
        crumbs={[{ label: tx('Paylaşımlarım') }]}
        title={tx('Paylaşımlarım')}
      />

      <QueryState isPending={query.isPending} error={query.error}>
        {shares.length === 0 ? (
          <EmptyState
            icon={Share2}
            message={tx('Henüz paylaşım yapılmamış.')}
            action={<p className="text-sm text-text-muted">{tx('Yedek dosyası detayından PlatformAdmin için paylaşım oluşturabilirsiniz.')}</p>}
          />
        ) : (
          <DataList
            rows={shares}
            columns={columns}
            keyExtractor={(share) => share.id}
            caption={tx('Paylaşımlarım')}
          />
        )}
      </QueryState>
    </div>
  )
}
