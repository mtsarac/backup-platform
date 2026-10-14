import type { ReactNode } from 'react'
import { PlatformAccessDenied } from '@/components/auth/platform-access-denied'
import { DataTable } from '@/components/data/data-table'
import { Pagination } from '@/components/data/pagination'
import { QueryState } from '@/components/data/query-state'
import { Button } from '@/components/ui/button'
import { Notice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { Fact, Facts, Section } from '@/components/ui/section'
import { PermissionBadges, SupportStatusPill } from '@/features/platform/support/session-table'
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
  if (error instanceof ApiError && error.status === 404) return <Notice tone="error">Destek oturumu bulunamadı veya bu tenant'a ait değil.</Notice>
  return <QueryState isPending={isPending} error={error}>{children}</QueryState>
}

function Summary({ summary }: { summary: SupportAccessSummary }) {
  return <Facts>{Object.entries(summaryLabels).map(([key, label]) => <Fact key={key} label={label}><span className="font-medium tabular-nums">{summary[key as keyof SupportAccessSummary]}</span></Fact>)}</Facts>
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
  return <div className="space-y-6">
    <PageHeader title="Destek erişimi" description="PlatformAdmin destek oturumları ve gerçekleştirilen işlemler. Bu alan salt okunurdur." />
    <AccessQueryState isPending={query.isPending} error={query.error}>
      {query.data && <div className="space-y-4">
        <DataTable rows={query.data.items} keyExtractor={(row) => row.id} emptyMessage="Henüz destek erişimi kaydı yok." columns={[
          { key: 'actor', header: 'İşlemi yapan', render: (row) => row.actorDisplayName ?? 'Bilinmiyor', wrap: true },
          { key: 'when', header: 'Zaman', render: (row) => <div>Başlangıç: {new Date(row.startedAt).toLocaleString('tr-TR')}<br />Bitiş: {new Date(row.expiresAt).toLocaleString('tr-TR')}</div> },
          { key: 'reason', header: 'Gerekçe', render: (row) => row.reason, wrap: true },
          { key: 'permissions', header: 'İzinler', render: (row) => <PermissionBadges permissions={row.permissions} />, wrap: true },
          { key: 'status', header: 'Durum', render: (row) => <SupportStatusPill status={row.status} /> },
          { key: 'summary', header: 'İşlem özeti', render: (row) => <Summary summary={row.summary} />, wrap: true },
          { key: 'detail', header: 'Etkinlik', render: (row) => <Button variant="outline" size="sm" onClick={() => update({ sessionId: row.id, activityPage: '', page })}>Etkinliği görüntüle</Button> },
        ]} />
        {query.data.totalCount > 0 && <Pagination label="Destek oturumu sayfaları" page={page} pageSize={query.data.pageSize} totalCount={query.data.totalCount} disabled={query.isFetching} onPageChange={(next) => update({ page: next })} />}
      </div>}
    </AccessQueryState>
    {selected && <SessionDetail key={selected} tenantId={tenantId} sessionId={selected} onClose={() => update({ sessionId: '', activityPage: '', page })} />}
  </div>
}

function SessionDetail({ tenantId, sessionId, onClose }: { tenantId: string; sessionId: string; onClose: () => void }) {
  const query = useSupportAccessSession(tenantId, sessionId)
  return <Section title="Destek oturumu ayrıntısı" description="Bu tenant için kaydedilen gerekçe, izinler ve erişim geçmişi." actions={<Button variant="outline" size="sm" onClick={onClose}>Ayrıntıyı kapat</Button>}>
    <AccessQueryState isPending={query.isPending} error={query.error}>{query.data && <div className="space-y-6">
      <Facts>
        <Fact label="İşlemi yapan">{query.data.actorDisplayName ?? 'Bilinmiyor'}</Fact>
        <Fact label="Durum"><SupportStatusPill status={query.data.status} /></Fact>
        <Fact label="Gerekçe">{query.data.reason}</Fact>
        <Fact label="İzinler"><PermissionBadges permissions={query.data.permissions} /></Fact>
        <Fact label="Başlangıç">{new Date(query.data.startedAt).toLocaleString('tr-TR')}</Fact>
        <Fact label="Süre sonu">{new Date(query.data.expiresAt).toLocaleString('tr-TR')}</Fact>
        <Fact label="Kapanış">{query.data.closedAt ? new Date(query.data.closedAt).toLocaleString('tr-TR') : '—'}</Fact>
        <Fact label="Kapanış gerekçesi">{query.data.closeReason ?? '—'}</Fact>
      </Facts>
      <div className="rounded-lg bg-surface-2 p-4"><Summary summary={query.data.summary} /></div>
      <SessionActivity tenantId={tenantId} sessionId={sessionId} />
    </div>}</AccessQueryState>
  </Section>
}

function SessionActivity({ tenantId, sessionId }: { tenantId: string; sessionId: string }) {
  const { page, update } = useListParams('activityPage')
  const query = useSupportAccessActivity(tenantId, sessionId, { page, pageSize: PLATFORM_PAGE_SIZE })
  return <div className="space-y-3"><h3 className="font-semibold">Etkinlik geçmişi</h3>
    <AccessQueryState isPending={query.isPending} error={query.error}>{query.data && <>
      <DataTable rows={query.data.items} keyExtractor={(row) => row.id} emptyMessage="Bu oturum için etkinlik kaydı yok." columns={[
        { key: 'at', header: 'Zaman', render: (row) => new Date(row.at).toLocaleString('tr-TR') },
        { key: 'action', header: 'İşlem', render: (row) => row.action, wrap: true },
        { key: 'target', header: 'Hedef', render: (row) => <div>{row.targetType}<p className="break-all font-mono text-xs text-text-muted">{row.targetId}</p></div>, wrap: true },
        { key: 'result', header: 'Sonuç', render: (row) => row.result ?? '—', wrap: true },
      ]} />
      {query.data.totalCount > 0 && <Pagination label="Destek etkinliği sayfaları" page={page} pageSize={query.data.pageSize} totalCount={query.data.totalCount} disabled={query.isFetching} onPageChange={(next) => update({ activityPage: next })} />}
    </>}</AccessQueryState>
  </div>
}
