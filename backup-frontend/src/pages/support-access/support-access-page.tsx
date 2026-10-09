import type { ReactNode } from 'react'
import { PlatformAccessDenied } from '@/components/auth/platform-access-denied'
import { DataTable } from '@/components/data/data-table'
import { Pagination } from '@/components/data/pagination'
import { QueryState } from '@/components/data/query-state'
import { Button } from '@/components/ui/button'
import { statusLabels } from '@/features/platform/support/presentation'
import { PermissionBadges } from '@/features/platform/support/session-table'
import { PLATFORM_PAGE_SIZE, useListParams } from '@/features/platform/use-list-params'
import { useSupportAccessActivity, useSupportAccessSession, useSupportAccessSessions } from '@/features/support-access/hooks'
import type { SupportAccessSummary } from '@/features/support-access/types'
import { useTenant } from '@/features/tenancy/use-tenant'
import { ApiError } from '@/lib/api/client'

const summaryLabels: Record<keyof SupportAccessSummary, string> = {
  artifactsViewed: 'Görüntülenen Artifact', downloadsStarted: 'Başlatılan indirme',
  downloadsCompleted: 'Tamamlanan indirme', downloadsFailed: 'Başarısız indirme',
  restoresRequested: 'İstenen Restore', restoresCancelled: 'İptal edilen Restore',
}

function AccessQueryState({ isPending, error, children }: { isPending: boolean; error: unknown; children: ReactNode }) {
  if (error instanceof ApiError && error.status === 403) return <PlatformAccessDenied description="Destek erişimi geçmişini yalnızca tenant Owner / Admin görüntüleyebilir." />
  if (error instanceof ApiError && error.status === 404) return <div role="alert" className="rounded-lg border border-border bg-surface p-6">Destek oturumu bulunamadı veya bu tenant'a ait değil.</div>
  return <QueryState isPending={isPending} error={error}>{children}</QueryState>
}

function Summary({ summary }: { summary: SupportAccessSummary }) {
  return <dl className="grid gap-2 text-sm sm:grid-cols-2">{Object.entries(summaryLabels).map(([key, label]) => <div key={key}><dt className="text-text-muted">{label}</dt><dd className="font-medium tabular-nums">{summary[key as keyof SupportAccessSummary]}</dd></div>)}</dl>
}

export function SupportAccessPage() {
  const { tenantId, canAdmin } = useTenant()
  if (!canAdmin) return <PlatformAccessDenied description="Destek erişimi geçmişini yalnızca tenant Owner / Admin görüntüleyebilir." />
  return <SupportAccessContent key={tenantId} tenantId={tenantId} />
}

function SupportAccessContent({ tenantId }: { tenantId: string }) {
  const { page, get, update } = useListParams()
  const query = useSupportAccessSessions(tenantId, { page, pageSize: PLATFORM_PAGE_SIZE })
  const selected = get('sessionId')
  return <div className="space-y-4">
    <div><h1 className="text-2xl font-semibold text-text-heading">Destek erişimi</h1><p className="mt-1 text-sm text-text-muted">PlatformAdmin destek oturumları ve gerçekleştirilen işlemler. Bu alan salt okunurdur.</p></div>
    <AccessQueryState isPending={query.isPending} error={query.error}>
      {query.data && <>
        <DataTable rows={query.data.items} keyExtractor={(row) => row.id} emptyMessage="Henüz destek erişimi kaydı yok." columns={[
          { key: 'actor', header: 'İşlemi yapan', render: (row) => row.actorDisplayName ?? 'Bilinmiyor', wrap: true },
          { key: 'when', header: 'Zaman', render: (row) => <div>Başlangıç: {new Date(row.startedAt).toLocaleString('tr-TR')}<br />Bitiş: {new Date(row.expiresAt).toLocaleString('tr-TR')}</div> },
          { key: 'reason', header: 'Gerekçe', render: (row) => row.reason, wrap: true },
          { key: 'permissions', header: 'İzinler', render: (row) => <PermissionBadges permissions={row.permissions} />, wrap: true },
          { key: 'status', header: 'Durum', render: (row) => statusLabels[row.status] },
          { key: 'summary', header: 'İşlem özeti', render: (row) => <Summary summary={row.summary} />, wrap: true },
          { key: 'detail', header: 'Etkinlik', render: (row) => <Button variant="outline" size="sm" onClick={() => update({ sessionId: row.id, activityPage: '', page })}>Etkinliği görüntüle</Button> },
        ]} />
        {query.data.totalCount > 0 && <Pagination label="Destek oturumu sayfaları" page={page} pageSize={query.data.pageSize} totalCount={query.data.totalCount} disabled={query.isFetching} onPageChange={(next) => update({ page: next })} />}
      </>}
    </AccessQueryState>
    {selected && <SessionDetail key={selected} tenantId={tenantId} sessionId={selected} onClose={() => update({ sessionId: '', activityPage: '', page })} />}
  </div>
}

function SessionDetail({ tenantId, sessionId, onClose }: { tenantId: string; sessionId: string; onClose: () => void }) {
  const query = useSupportAccessSession(tenantId, sessionId)
  return <section aria-label="Destek oturumu ayrıntısı" className="space-y-4 rounded-lg border border-border bg-surface p-4">
    <div className="flex items-center justify-between gap-2"><h2 className="text-lg font-semibold">Destek oturumu ayrıntısı</h2><Button variant="outline" size="sm" onClick={onClose}>Ayrıntıyı kapat</Button></div>
    <AccessQueryState isPending={query.isPending} error={query.error}>{query.data && <>
      <dl className="grid gap-3 text-sm sm:grid-cols-2">
        <div><dt className="text-text-muted">İşlemi yapan</dt><dd>{query.data.actorDisplayName ?? 'Bilinmiyor'}</dd></div>
        <div><dt className="text-text-muted">Durum</dt><dd>{statusLabels[query.data.status]}</dd></div>
        <div><dt className="text-text-muted">Gerekçe</dt><dd className="break-words">{query.data.reason}</dd></div>
        <div><dt className="text-text-muted">İzinler</dt><dd><PermissionBadges permissions={query.data.permissions} /></dd></div>
        <div><dt className="text-text-muted">Başlangıç</dt><dd>{new Date(query.data.startedAt).toLocaleString('tr-TR')}</dd></div>
        <div><dt className="text-text-muted">Süre sonu</dt><dd>{new Date(query.data.expiresAt).toLocaleString('tr-TR')}</dd></div>
        <div><dt className="text-text-muted">Kapanış</dt><dd>{query.data.closedAt ? new Date(query.data.closedAt).toLocaleString('tr-TR') : '—'}</dd></div>
        <div><dt className="text-text-muted">Kapanış gerekçesi</dt><dd className="break-words">{query.data.closeReason ?? '—'}</dd></div>
      </dl>
      <Summary summary={query.data.summary} />
      <SessionActivity tenantId={tenantId} sessionId={sessionId} />
    </>}</AccessQueryState>
  </section>
}

function SessionActivity({ tenantId, sessionId }: { tenantId: string; sessionId: string }) {
  const { page, update } = useListParams('activityPage')
  const query = useSupportAccessActivity(tenantId, sessionId, { page, pageSize: PLATFORM_PAGE_SIZE })
  return <div className="space-y-3"><h3 className="font-semibold">Etkinlik geçmişi</h3>
    <AccessQueryState isPending={query.isPending} error={query.error}>{query.data && <>
      <DataTable rows={query.data.items} keyExtractor={(row) => row.id} emptyMessage="Bu oturum için etkinlik kaydı yok." columns={[
        { key: 'at', header: 'Zaman', render: (row) => new Date(row.at).toLocaleString('tr-TR') },
        { key: 'action', header: 'İşlem', render: (row) => row.action, wrap: true },
        { key: 'target', header: 'Hedef', render: (row) => <div>{row.targetType}<p className="text-xs text-text-muted">{row.targetId}</p></div>, wrap: true },
        { key: 'result', header: 'Sonuç', render: (row) => row.result ?? '—', wrap: true },
      ]} />
      {query.data.totalCount > 0 && <Pagination label="Destek etkinliği sayfaları" page={page} pageSize={query.data.pageSize} totalCount={query.data.totalCount} disabled={query.isFetching} onPageChange={(next) => update({ activityPage: next })} />}
    </>}</AccessQueryState>
  </div>
}
