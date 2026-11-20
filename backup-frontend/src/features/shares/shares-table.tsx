import { useState, useContext, type ReactNode } from 'react'
import { Share2 } from 'lucide-react'
import { Link } from 'react-router-dom'
import { DataList, type Column } from '@/components/data/data-list'
import { StatusPill } from '@/components/data/status-pill'
import { Button } from '@/components/ui/button'
import { EmptyState } from '@/components/ui/empty-state'
import { FilterField } from '@/components/ui/filters'
import { linkClass, rowLinkClass } from '@/components/ui/form-styles'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { Select } from '@/components/ui/text-input'
import { compareDate, compareText } from '@/features/lists/compare'
import { ListEmpty } from '@/features/lists/list-empty'
import { ListToolbar } from '@/features/lists/list-toolbar'
import { useClientList } from '@/features/lists/use-client-list'
import { sortProps, useListState } from '@/features/lists/use-list-state'
import { useAuth } from '@/features/auth/use-auth'
import { TenantContext } from '@/features/tenancy/tenant-context'
import { formatBytes, formatDateTime } from '@/lib/format'
import { describeFailure } from '@/lib/api/failure-messages'
import { tx, useLanguage } from '@/lib/i18n'
import { ShareActionDialogs } from './share-dialogs'
import { canExtendShare, canManageShare, type BackupShareView, type DownloadAttemptView } from './types'
import { prepareDownload } from '@/features/artifacts/api'
import { ArtifactRunLink } from '@/features/artifacts/artifact-run-link'

const listConfig = {
  search: true,
  filters: { status: ['Active', 'Expired', 'Revoked'] },
  sort: { first: { tenant: 'asc', artifact: 'asc', creator: 'asc', role: 'asc', createdAt: 'desc', expiresAt: 'desc', status: 'asc', reason: 'asc' }, default: { key: 'createdAt', dir: 'desc' } },
} as const

const comparators = {
  tenant: (a: BackupShareView, b: BackupShareView) => compareText(a.tenantName || a.tenantId, b.tenantName || b.tenantId),
  artifact: (a: BackupShareView, b: BackupShareView) => compareText(a.artifactName || tx('Yedek dosyası'), b.artifactName || tx('Yedek dosyası')),
  creator: (a: BackupShareView, b: BackupShareView) => compareText(a.createdByDisplayName || a.createdByUserId, b.createdByDisplayName || b.createdByUserId),
  role: (a: BackupShareView, b: BackupShareView) => compareText(a.createdByRole, b.createdByRole),
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
  const { user } = useAuth()
  const tenantContext = useContext(TenantContext)
  const state = useListState(listConfig)
  const list = useClientList(shares, state, {
    searchText: share => `${share.artifactName ?? ''} ${share.createdByDisplayName || share.createdByUserId} ${share.createdByRole} ${share.reason ?? ''} ${showTenant ? share.tenantName || share.tenantId : ''}`,
    filters: { status: (share, value) => share.status === value },
    comparators,
  })

  const [extendTarget, setExtendTarget] = useState<BackupShareView | null>(null)
  const [revokeTarget, setRevokeTarget] = useState<BackupShareView | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [errorNotice, setErrorNotice] = useState<string | null>(null)
  const [downloadingId, setDownloadingId] = useState<string | null>(null)

  async function handleDownload(share: BackupShareView) {
    setDownloadingId(share.id)
    setErrorNotice(null)
    setNotice(null)
    try {
      const prepared = await prepareDownload(share.tenantId, share.artifactId, undefined, undefined, share.id)
      const anchor = document.createElement('a')
      anchor.href = prepared.downloadUrl
      anchor.download = prepared.filename
      document.body.appendChild(anchor)
      anchor.click()
      anchor.remove()
      setNotice(tx('İndirme başlatıldı. Dosya tarayıcınız tarafından kaydediliyor.'))
    } catch (err: unknown) {
      let msg = tx('İndirme başlatılamadı.')
      if (err && typeof err === 'object') {
        const errorObj = err as { code?: string; reason?: string; error?: string; status?: number; message?: string }
        const code = errorObj.code || errorObj.error || errorObj.reason || errorObj.message || ''
        const status = errorObj.status
        if (code === 'replica_unavailable' || code === 'no_downloadable_replica') {
          msg = tx('Kopya şu anda indirme için kullanılamıyor.')
        } else if (code === 'share_revoked') {
          msg = tx('Paylaşım iptal edildiği için indirme yapılamaz.')
        } else if (code === 'share_expired' || status === 403 || code.includes('403')) {
          msg = tx('İndirme yetkisi reddedildi veya paylaşım süresi doldu.')
        }
      }
      setErrorNotice(msg)
    } finally {
      setDownloadingId(null)
    }
  }

  const columns: Column<BackupShareView>[] = [
    ...(showTenant
      ? [
          {
            key: 'tenant',
            header: tx('Tenant'),
            sortable: true,
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
      sortable: true,
      card: 'title' as const,
      render: (share: BackupShareView) => (
        <ArtifactRunLink className={`${linkClass} ${rowLinkClass}`} tenantId={share.tenantId} artifactId={share.artifactId}>
          {share.artifactName || tx('Yedek dosyası')}
        </ArtifactRunLink>
      ),
    },
    {
      key: 'creator',
      header: tx('Oluşturan'),
      sortable: true,
      render: (share: BackupShareView) => share.createdByDisplayName || share.createdByUserId,
    },
    {
      key: 'role',
      header: tx('Rol'),
      sortable: true,
      render: (share: BackupShareView) => share.createdByRole,
    },
    {
      key: 'createdAt',
      header: tx('Oluşturulma'),
      sortable: true,
      render: (share: BackupShareView) => formatDateTime(share.createdAtUtc),
    },
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
      header: tx('İşlemler'),
      render: (share: BackupShareView) => {
        const canManage = canManageShare(share, user?.role)
        const canExtend = canExtendShare(share, user?.role)
        const canDownload = user?.role === 'PlatformAdmin' && share.status === 'Active'
        if (!canManage && !canDownload) {
          return '—'
        }
        return (
          <div className="flex gap-2 justify-end">
            {canDownload && (
              <Button
                size="sm"
                variant="outline"
                disabled={downloadingId === share.id}
                onClick={() => void handleDownload(share)}
              >
                {downloadingId === share.id ? tx('İndirme hazırlanıyor…') : tx('İndir')}
              </Button>
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
      {errorNotice && (
        <ErrorNotice what={errorNotice} />
      )}
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

export interface DownloadAttemptsTableProps {
  attempts: DownloadAttemptView[]
  showTenant?: boolean
  emptyMessage?: string
  /** Present while older attempts remain on the server. */
  onLoadMore?: () => void
  onRefresh?: () => void
  loadingMore?: boolean
}

export function DownloadAttemptsTable({
  attempts,
  showTenant = false,
  emptyMessage,
  onLoadMore,
  onRefresh,
  loadingMore = false,
}: DownloadAttemptsTableProps) {
  useLanguage()

  const columns: Column<DownloadAttemptView>[] = [
    ...(showTenant
      ? [
          {
            key: 'tenant',
            header: tx('Tenant'),
            render: (attempt: DownloadAttemptView) => attempt.tenantName || attempt.tenantId,
          },
        ]
      : []),
    {
      key: 'artifact',
      header: tx('Yedek dosyası'),
      card: 'title' as const,
      render: (attempt: DownloadAttemptView) => attempt.artifactName || attempt.artifactId,
    },
    {
      key: 'share',
      header: tx('Paylaşım'),
      render: (attempt: DownloadAttemptView) => attempt.shareId || '—',
    },
    {
      key: 'user',
      header: tx('Kullanıcı'),
      render: (attempt: DownloadAttemptView) => attempt.initiatingUserDisplayName || attempt.initiatingUserId,
    },
    {
      key: 'startedAt',
      header: tx('Başlangıç'),
      render: (attempt: DownloadAttemptView) => formatDateTime(attempt.startedAtUtc),
    },
    {
      key: 'endedAt',
      header: tx('Bitiş'),
      render: (attempt: DownloadAttemptView) => (attempt.endedAtUtc ? formatDateTime(attempt.endedAtUtc) : '—'),
    },
    {
      key: 'result',
      header: tx('Sonuç'),
      card: 'status' as const,
      render: (attempt: DownloadAttemptView) => <StatusPill domain="downloadAttempt" status={attempt.result} />,
    },
    {
      key: 'bytes',
      header: tx('Aktarılan veri'),
      render: (attempt: DownloadAttemptView) => formatBytes(attempt.bytesTransferred),
    },
    {
      key: 'failureReason',
      header: tx('Hata / Red gerekçesi'),
      wrap: true,
      render: (attempt: DownloadAttemptView) => {
        if (!attempt.failureReason) return '—'
        const failure = describeFailure(attempt.failureReason)
        return <ErrorNotice {...failure} technical={failure.technical.length ? failure.technical : [{ label: tx('Sunucu mesajı'), value: attempt.failureReason }]} />
      },
    },
  ]

  if (attempts.length === 0) {
    return (
      <EmptyState
        icon={Share2}
        message={emptyMessage || tx('Henüz indirme girişimi kaydı yok.')}
        action={onRefresh && <Button onClick={onRefresh}>{tx('Yenile')}</Button>}
      />
    )
  }

  return (
    <div className="space-y-3">
      <DataList
        emptyMessage={tx('Henüz indirme girişimi kaydı yok.')}
        rows={attempts}
        columns={columns}
        keyExtractor={(attempt) => attempt.id}
        caption={tx('İndirme girişimleri')}
      />
      {onLoadMore && (
        <Button variant="outline" size="sm" disabled={loadingMore} onClick={onLoadMore}>
          {loadingMore ? tx('Yükleniyor…') : tx('Daha eski girişimleri göster')}
        </Button>
      )}
    </div>
  )
}
