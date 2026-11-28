import { useState, useContext, type ReactNode } from 'react'
import { Share2 } from 'lucide-react'
import { Link } from 'react-router-dom'
import { useLargeScreen } from '@/hooks/use-large-screen'
import { DataList, type Column } from '@/components/data/data-list'
import { StatusPill } from '@/components/data/status-pill'
import { Button } from '@/components/ui/button'
import { FilterField } from '@/components/ui/filters'
import { linkClass, rowLinkClass } from '@/components/ui/form-styles'
import { Notice } from '@/components/ui/notice'
import { Select } from '@/components/ui/text-input'
import { compareDate, compareText } from '@/features/lists/compare'
import { ListEmpty } from '@/features/lists/list-empty'
import { ListToolbar } from '@/features/lists/list-toolbar'
import { useClientList } from '@/features/lists/use-client-list'
import { sortProps, useListState } from '@/features/lists/use-list-state'
import { accountRoleLabel } from '@/features/auth/role-label'
import { useAuth } from '@/features/auth/use-auth'
import { TenantContext } from '@/features/tenancy/tenant-context'
import { formatDateTime } from '@/lib/format'
import { tx, useLanguage } from '@/lib/i18n'
import { ShareDownload } from './share-download'
import { ShareActionDialogs } from './share-dialogs'
import { canExtendShare, canManageShare, type BackupShareView } from './types'
import { ArtifactRunLink } from '@/features/artifacts/artifact-run-link'

const listConfig = {
  search: true,
  filters: { status: ['Active', 'Expired', 'Revoked'] },
  sort: { first: { tenant: 'asc', artifact: 'asc', creator: 'asc', createdAt: 'desc', expiresAt: 'desc', status: 'asc', reason: 'asc' }, default: { key: 'createdAt', dir: 'desc' } },
} as const

const comparators = {
  tenant: (a: BackupShareView, b: BackupShareView) => compareText(a.tenantName || a.tenantId, b.tenantName || b.tenantId),
  artifact: (a: BackupShareView, b: BackupShareView) => compareText(a.artifactName || tx('Yedek dosyası'), b.artifactName || tx('Yedek dosyası')),
  creator: (a: BackupShareView, b: BackupShareView) => compareText(a.createdByDisplayName || a.createdByUserId, b.createdByDisplayName || b.createdByUserId),
  createdAt: (a: BackupShareView, b: BackupShareView) => compareDate(a.createdAtUtc, b.createdAtUtc),
  expiresAt: (a: BackupShareView, b: BackupShareView) => compareDate(a.expiresAtUtc, b.expiresAtUtc),
  status: (a: BackupShareView, b: BackupShareView) => compareText(a.status, b.status),
  reason: (a: BackupShareView, b: BackupShareView) => compareText(a.reason, b.reason),
}

export interface SharesTableProps {
  shares: BackupShareView[]
  showTenant?: boolean
  emptyMessage?: string
  emptyAction?: ReactNode
  caption?: string
  tenantFilter?: ReactNode
}

export function SharesTable({
  shares,
  showTenant = false,
  emptyMessage,
  emptyAction,
  caption,
  tenantFilter,
}: SharesTableProps) {
  useLanguage()
  const large = useLargeScreen()
  const { user } = useAuth()
  const tenantContext = useContext(TenantContext)
  const state = useListState(listConfig)
  const list = useClientList(shares, state, {
    searchText: share => `${share.artifactName ?? ''} ${share.createdByDisplayName || share.createdByUserId} ${accountRoleLabel(share.createdByRole)} ${share.reason ?? ''} ${showTenant ? share.tenantName || share.tenantId : ''}`,
    filters: { status: (share, value) => share.status === value },
    comparators,
  })

  const [extendTarget, setExtendTarget] = useState<BackupShareView | null>(null)
  const [revokeTarget, setRevokeTarget] = useState<BackupShareView | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

  const columns: Column<BackupShareView>[] = [
    {
      key: 'artifact',
      header: tx('Yedek dosyası'),
      sortable: true,
      card: 'title' as const,
      render: (share: BackupShareView) => (
        <>
          <span className="block">
            <ArtifactRunLink className={`${linkClass} ${rowLinkClass}`} tenantId={share.tenantId} artifactId={share.artifactId}>
              {share.artifactName || tx('Yedek dosyası')}
            </ArtifactRunLink>
          </span>
          {showTenant && (
            <span className="block text-sm">
              <Link className={`${linkClass} ${rowLinkClass}`} to={`/platform/tenants/${share.tenantId}`}>
                {share.tenantName || share.tenantId}
              </Link>
            </span>
          )}
        </>
      ),
    },
    {
      key: 'creator',
      header: tx('Oluşturan'),
      sortable: true,
      render: (share: BackupShareView) => (
        <>
          {share.createdByDisplayName || share.createdByUserId}
          <span className="block text-sm text-text-muted"><span>{accountRoleLabel(share.createdByRole)}</span>{large && <> · <span>{formatDateTime(share.createdAtUtc)}</span></>}</span>
        </>
      ),
    },
    // The table folds the creation date under the creator to keep reason and actions in view; the card list sorts by it from the menu.
    ...(large ? [] : [{
      key: 'createdAt',
      header: tx('Oluşturulma'),
      sortable: true,
      card: 'hidden' as const,
      render: (share: BackupShareView) => formatDateTime(share.createdAtUtc),
    }]),
    {
      key: 'expiresAt',
      header: tx('Bitiş'),
      sortable: true,
      render: (share: BackupShareView) => formatDateTime(share.expiresAtUtc),
    },
    {
      key: 'status',
      header: tx('Durum'),
      sortable: true,
      card: 'status' as const,
      render: (share: BackupShareView) => <StatusPill domain="share" status={share.status} />,
    },
    {
      key: 'reason',
      header: tx('Gerekçe'),
      sortable: true,
      wrap: true,
      render: (share: BackupShareView) => share.reason || '—',
    },
    {
      key: 'actions',
      card: 'actions' as const,
      header: tx('İşlemler'),
      render: (share: BackupShareView) => {
        const canManage = canManageShare(share, user?.role)
        const canExtend = canExtendShare(share, user?.role)
        const canDownload = user?.role === 'PlatformAdmin' && share.status === 'Active'
        if (!canManage && !canDownload) {
          return '—'
        }
        return (
          <div className="flex flex-col items-stretch gap-2 max-lg:flex-row max-lg:flex-wrap">
            {canDownload && (
              <ShareDownload share={share} />
            )}
            {canManage && (
              <>
                {canExtend && (
                  <Button
                    size="sm"
                    variant="outline"
                    onClick={() => setExtendTarget(share)}
                  >
                    {tx('Süreyi uzat')}
                  </Button>
                )}
                <Button
                  size="sm"
                  variant="outline"
                  onClick={() => setRevokeTarget(share)}
                >
                  {tx('İptal et')}
                </Button>
              </>
            )}
          </div>
        )
      },
    },
  ]


  return (
    <div className="space-y-4">
      {notice && <Notice tone="success">{notice}</Notice>}
      <ListToolbar state={state} label={tx('Paylaşım filtreleri')} total={list.total} search={{ label: tx('Paylaşım ara') }}>
        {tenantFilter}
        <FilterField label={tx('Durum')}>
          <Select aria-label={tx('Durum')} value={state.filters.status} onChange={event => state.setFilter('status', event.target.value)}>
            <option value="">{tx('Tümü')}</option>
            <option value="Active">{tx('Aktif')}</option>
            <option value="Expired">{tx('Süresi doldu')}</option>
            <option value="Revoked">{tx('İptal edildi')}</option>
          </Select>
        </FilterField>
      </ListToolbar>
      {shares.length === 0 && !state.filtering
        ? <ListEmpty icon={Share2} message={emptyMessage || tx('Henüz paylaşım yapılmamış.')} action={emptyAction} />
        :
      <DataList
        emptyMessage={tx('Seçilen filtrelere uygun paylaşım bulunamadı.')}
        onClearFilters={state.filtering ? state.clear : undefined}
        rows={list.rows}
        columns={columns}
        keyExtractor={(share) => share.id}
        caption={caption || tx('Paylaşımlar')}
        sort={sortProps(state)}
      />}
      <ShareActionDialogs
        tenantId={extendTarget?.tenantId || revokeTarget?.tenantId || tenantContext?.tenantId || ''}
        extendTarget={extendTarget}
        revokeTarget={revokeTarget}
        onClose={() => {
          setExtendTarget(null)
          setRevokeTarget(null)
        }}
        onSuccess={(msg) => setNotice(msg)}
      />
    </div>
  )
}
