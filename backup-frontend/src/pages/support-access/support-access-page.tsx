import { tx, useLanguage } from '@/lib/i18n'
import type { ReactNode } from 'react'
import { AccessDenied } from '@/components/auth/access-denied'
import { DataList } from '@/components/data/data-list'
import { Pagination } from '@/components/data/pagination'
import { QueryState } from '@/components/data/query-state'
import { Button } from '@/components/ui/button'
import { ErrorNotice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { Fact, Facts, Section } from '@/components/ui/section'
import { PermissionBadges } from '@/features/platform/support/session-table'
import { PLATFORM_PAGE_SIZE, useListParams } from '@/features/platform/use-list-params'
import { useSupportAccessActivity, useSupportAccessSession, useSupportAccessSessions } from '@/features/support-access/hooks'
import type { SupportAccessSummary } from '@/features/support-access/types'
import { useTenant } from '@/features/tenancy/use-tenant'
import { ApiError } from '@/lib/api/client'
import { formatDateTime } from '@/lib/format'
import { StatusPill } from '@/components/data/status-pill'
import { notFound } from '@/lib/api/failure-messages'

const summaryLabels: Record<keyof SupportAccessSummary, string> = {
  get artifactsViewed() { return tx("Görüntülenen Artifact") }, get downloadsStarted() { return tx("Başlatılan indirme") },
  get downloadsCompleted() { return tx("Tamamlanan indirme") }, get downloadsFailed() { return tx("Başarısız indirme") },
  get restoresRequested() { return tx("İstenen Restore") }, get restoresCancelled() { return tx("İptal edilen Restore") },
}

function AccessQueryState({ isPending, error, children }: { isPending: boolean; error: unknown; children: ReactNode }) {
  useLanguage()
  if (error instanceof ApiError && error.status === 403) return <AccessDenied what={tx("Destek erişimi geçmişini yalnızca tenant Owner / Admin görüntüleyebilir.")} />
  if (error instanceof ApiError && error.status === 404) return <ErrorNotice {...notFound(tx("Destek oturumu bulunamadı veya bu tenant'a ait değil."))} />
  return <QueryState isPending={isPending} error={error}>{children}</QueryState>
}

function Summary({ summary }: { summary: SupportAccessSummary }) {
  useLanguage()
  return <Facts>{Object.entries(summaryLabels).map(([key, label]) => <Fact key={key} label={label}><span className="font-medium tabular-nums">{summary[key as keyof SupportAccessSummary]}</span></Fact>)}</Facts>
}

export function SupportAccessPage() {
  useLanguage()
  const { tenantId, canAdmin } = useTenant()
  if (!canAdmin) return <AccessDenied what={tx("Destek erişimi geçmişini yalnızca tenant Owner / Admin görüntüleyebilir.")} />
  return <SupportAccessContent key={tenantId} tenantId={tenantId} />
}

function SupportAccessContent({ tenantId }: { tenantId: string }) {
  useLanguage()
  const { page, get, update } = useListParams()
  const query = useSupportAccessSessions(tenantId, { page, pageSize: PLATFORM_PAGE_SIZE })
  const selected = get('sessionId')
  return <div className="space-y-6">
    <PageHeader title={tx("Destek erişimi")} description={tx("PlatformAdmin destek oturumları ve gerçekleştirilen işlemler. Bu alan salt okunurdur.")} />
    <AccessQueryState isPending={query.isPending} error={query.error}>
      {query.data && <div className="space-y-4">
        <DataList rows={query.data.items} keyExtractor={(row) => row.id} emptyMessage={tx("Henüz destek erişimi kaydı yok.")} columns={[
          { key: 'actor', header: tx('İşlemi yapan'), render: (row) => row.actorDisplayName ?? tx('Bilinmiyor'), wrap: true },
          { key: 'when', header: tx('Zaman'), render: (row) => <div>{tx("Başlangıç:")}{' '}{formatDateTime(row.startedAt)}<br />{tx("Bitiş:")}{' '}{formatDateTime(row.expiresAt)}</div> },
          { key: 'reason', header: tx('Gerekçe'), render: (row) => row.reason, wrap: true },
          { key: 'permissions', card: 'hidden', header: tx('İzinler'), render: (row) => <PermissionBadges permissions={row.permissions} />, wrap: true },
          { key: 'status', card: 'status', header: tx('Durum'), render: (row) => <StatusPill domain="supportSession" status={row.status} /> },
          { key: 'summary', header: tx('İşlem özeti'), render: (row) => <Summary summary={row.summary} />, wrap: true },
          { key: 'detail', card: 'actions', header: tx('Etkinlik'), render: (row) => <Button variant="outline" size="sm" onClick={() => update({ sessionId: row.id, activityPage: '', page })}>{tx("Etkinliği görüntüle")}</Button> },
        ]} />
        {query.data.totalCount > 0 && <Pagination label={tx("Destek oturumu sayfaları")} page={page} pageSize={query.data.pageSize} totalCount={query.data.totalCount} disabled={query.isFetching} onPageChange={(next) => update({ page: next })} />}
      </div>}
    </AccessQueryState>
    {selected && <SessionDetail key={selected} tenantId={tenantId} sessionId={selected} onClose={() => update({ sessionId: '', activityPage: '', page })} />}
  </div>
}

function SessionDetail({ tenantId, sessionId, onClose }: { tenantId: string; sessionId: string; onClose: () => void }) {
  useLanguage()
  const query = useSupportAccessSession(tenantId, sessionId)
  return <Section title={tx("Destek oturumu ayrıntısı")} description={tx("Bu tenant için kaydedilen gerekçe, izinler ve erişim geçmişi.")} actions={<Button variant="outline" size="sm" onClick={onClose}>{tx("Ayrıntıyı kapat")}</Button>}>
    <AccessQueryState isPending={query.isPending} error={query.error}>{query.data && <div className="space-y-6">
      <Facts>
        <Fact label={tx("İşlemi yapan")}>{query.data.actorDisplayName ?? tx('Bilinmiyor')}</Fact>
        <Fact label={tx("Durum")}><StatusPill domain="supportSession" status={query.data.status} /></Fact>
        <Fact label={tx("Gerekçe")}>{query.data.reason}</Fact>
        <Fact label={tx("İzinler")}><PermissionBadges permissions={query.data.permissions} /></Fact>
        <Fact label={tx("Başlangıç")}>{formatDateTime(query.data.startedAt)}</Fact>
        <Fact label={tx("Süre sonu")}>{formatDateTime(query.data.expiresAt)}</Fact>
        <Fact label={tx("Kapanış")}>{query.data.closedAt ? formatDateTime(query.data.closedAt) : '—'}</Fact>
        <Fact label={tx("Kapanış gerekçesi")}>{query.data.closeReason ?? '—'}</Fact>
      </Facts>
      <div className="rounded-lg bg-surface-2 p-4"><Summary summary={query.data.summary} /></div>
      <SessionActivity tenantId={tenantId} sessionId={sessionId} />
    </div>}</AccessQueryState>
  </Section>
}

function SessionActivity({ tenantId, sessionId }: { tenantId: string; sessionId: string }) {
  useLanguage()
  const { page, update } = useListParams('activityPage')
  const query = useSupportAccessActivity(tenantId, sessionId, { page, pageSize: PLATFORM_PAGE_SIZE })
  return <div className="space-y-3"><h3 className="font-semibold">{tx("Etkinlik geçmişi")}</h3>
    <AccessQueryState isPending={query.isPending} error={query.error}>{query.data && <>
      <DataList rows={query.data.items} keyExtractor={(row) => row.id} emptyMessage={tx("Bu oturum için etkinlik kaydı yok.")} columns={[
        { key: 'at', header: tx('Zaman'), render: (row) => formatDateTime(row.at) },
        { key: 'action', header: tx('İşlem'), render: (row) => row.action, wrap: true },
        { key: 'target', card: 'hidden', header: tx('Hedef'), render: (row) => <div>{row.targetType}<p className="break-all font-mono text-xs text-text-muted">{row.targetId}</p></div>, wrap: true },
        { key: 'result', header: tx('Sonuç'), render: (row) => row.result ?? '—', wrap: true },
      ]} />
      {query.data.totalCount > 0 && <Pagination label={tx("Destek etkinliği sayfaları")} page={page} pageSize={query.data.pageSize} totalCount={query.data.totalCount} disabled={query.isFetching} onPageChange={(next) => update({ activityPage: next })} />}
    </>}</AccessQueryState>
  </div>
}
