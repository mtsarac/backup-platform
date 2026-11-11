import type { ReactNode } from 'react'
import { Share2 } from 'lucide-react'
import { Link } from 'react-router-dom'
import { DataList, type Column } from '@/components/data/data-list'
import { StatusPill } from '@/components/data/status-pill'
import { EmptyState } from '@/components/ui/empty-state'
import { linkClass, rowLinkClass } from '@/components/ui/form-styles'
import { formatDateTime } from '@/lib/format'
import { tx, useLanguage } from '@/lib/i18n'
import type { BackupShareView } from './types'

export interface SharesTableProps {
  shares: BackupShareView[]
  showTenant?: boolean
  emptyMessage?: string
  emptyAction?: ReactNode
  caption?: string
}

export function SharesTable({
  shares,
  showTenant = false,
  emptyMessage,
  emptyAction,
  caption,
}: SharesTableProps) {
  useLanguage()

  const columns: Column<BackupShareView>[] = [
    ...(showTenant
      ? [
          {
            key: 'tenant',
            header: tx('Tenant'),
            render: (share: BackupShareView) => (
              <Link className={`${linkClass} ${rowLinkClass}`} to={`/platform/tenants/${share.tenantId}`}>
                {share.tenantName || share.tenantId}
              </Link>
            ),
          },
        ]
      : []),
    {
      key: 'artifact',
      header: tx('Yedek dosyası'),
      card: 'title' as const,
      render: (share: BackupShareView) => (
        <Link className={`${linkClass} ${rowLinkClass}`} to={`/tenants/${share.tenantId}/artifacts/${share.artifactId}`}>
          {share.artifactName || tx('Yedek dosyası')}
        </Link>
      ),
    },
    {
      key: 'creator',
      header: tx('Oluşturan'),
      render: (share: BackupShareView) => share.createdByDisplayName || share.createdByUserId,
    },
    {
      key: 'role',
      header: tx('Rol'),
      render: (share: BackupShareView) => share.createdByRole,
    },
    {
      key: 'createdAt',
      header: tx('Oluşturulma'),
      render: (share: BackupShareView) => formatDateTime(share.createdAtUtc),
    },
    {
      key: 'expiresAt',
      header: tx('Bitiş'),
      render: (share: BackupShareView) => formatDateTime(share.expiresAtUtc),
    },
    {
      key: 'status',
      header: tx('Durum'),
      card: 'status' as const,
      render: (share: BackupShareView) => <StatusPill domain="share" status={share.status} />,
    },
    {
      key: 'reason',
      header: tx('Gerekçe'),
      wrap: true,
      render: (share: BackupShareView) => share.reason || '—',
    },
  ]

  if (shares.length === 0) {
    return (
      <EmptyState
        icon={Share2}
        message={emptyMessage || tx('Henüz paylaşım yapılmamış.')}
        action={emptyAction}
      />
    )
  }

  return (
    <DataList
      rows={shares}
      columns={columns}
      keyExtractor={(share) => share.id}
      caption={caption || tx('Paylaşımlar')}
    />
  )
}
