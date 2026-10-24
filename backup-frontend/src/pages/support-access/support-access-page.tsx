import { tx, useLanguage } from '@/lib/i18n'
import type { ReactNode } from 'react'
import { Link } from 'react-router-dom'
import { AccessDenied } from '@/components/auth/access-denied'
import { Pagination } from '@/components/data/pagination'
import { QueryState } from '@/components/data/query-state'
import { RelativeTime } from '@/components/data/relative-time'
import { StatusPill } from '@/components/data/status-pill'
import { Button } from '@/components/ui/button'
import { EmptyState } from '@/components/ui/empty-state'
import { linkClass } from '@/components/ui/form-styles'
import { ErrorNotice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { Fact, Facts, Section } from '@/components/ui/section'
import { TechnicalDetails } from '@/components/ui/technical-details'
import { PermissionBadges } from '@/features/platform/support/session-table'
import { PLATFORM_PAGE_SIZE, useListParams } from '@/features/platform/use-list-params'
import { useSupportAccessActivity, useSupportAccessSession, useSupportAccessSessions } from '@/features/support-access/hooks'
import { supportActionLabels } from '@/features/support-access/labels'
import type { SupportAccessSession, SupportAccessSummary } from '@/features/support-access/types'
import { useTenant } from '@/features/tenancy/use-tenant'
import { ApiError } from '@/lib/api/client'
import { formatDateTime } from '@/lib/format'
import { notFound } from '@/lib/api/failure-messages'

const summaryLabels: Record<keyof SupportAccessSummary, string> = {
  get artifactsViewed() { return tx('Görüntülenen yedek dosyası') }, get downloadsStarted() { return tx('Başlatılan indirme') },
  get downloadsCompleted() { return tx('Tamamlanan indirme') }, get downloadsFailed() { return tx('Başarısız indirme') },
  get restoresRequested() { return tx('İstenen geri yükleme') }, get restoresCancelled() { return tx('İptal edilen geri yükleme') },
}

function AccessQueryState({ isPending, error, children }: { isPending: boolean; error: unknown; children: ReactNode }) {
  useLanguage()
  if (error instanceof ApiError && error.status === 403) return <AccessDenied what={tx('Destek erişimi geçmişini yalnızca tenant Owner / Admin görüntüleyebilir.')} />
  if (error instanceof ApiError && error.status === 404) return <ErrorNotice {...notFound(tx("Destek oturumu bulunamadı veya bu tenant'a ait değil."))} />
  return <QueryState isPending={isPending} error={error}>{children}</QueryState>
}

function sessionDetails(session: SupportAccessSession) {
  return [
    { label: tx('Oturum kimliği'), value: session.id },
    { label: tx('Ham durum'), value: session.status },
    { label: tx('İzin kodları'), value: session.permissions.join(', ') },
    { label: tx('Başlangıç'), value: formatDateTime(session.startedAt) },
    { label: tx('Süre sonu'), value: formatDateTime(session.expiresAt) },
    { label: tx('Kapanış'), value: formatDateTime(session.closedAt) },
    { label: tx('Kapanış gerekçesi'), value: session.closeReason ?? '—' },
    { label: tx('Kayıt zamanı'), value: formatDateTime(session.createdAt) },
    ...Object.entries(summaryLabels).map(([key, label]) => ({ label, value: String(session.summary[key as keyof SupportAccessSummary]) })),
  ]
}

export function SupportAccessPage() {
  useLanguage()
  const { tenantId, canAdmin } = useTenant()
  if (!canAdmin) return <AccessDenied what={tx('Destek erişimi geçmişini yalnızca tenant Owner / Admin görüntüleyebilir.')} />
  return <SupportAccessContent key={tenantId} tenantId={tenantId} />
}

function SupportAccessContent({ tenantId }: { tenantId: string }) {
  useLanguage()
  const { page, get, update } = useListParams()
  const query = useSupportAccessSessions(tenantId, { page, pageSize: PLATFORM_PAGE_SIZE })
  const selected = get('sessionId')
  return <div className="space-y-6">
    <PageHeader title={tx('Destek erişimi')} description={tx('Hangi platform çalışanı verilerinize ne zaman, neden ve nasıl erişti? Bu geçmiş değiştirilemez.')} />
    <AccessQueryState isPending={query.isPending} error={query.error}>
      {query.data && <div className="space-y-4">
        {query.data.items.length === 0 ? <EmptyState message={tx('Henüz destek erişimi kaydı yok.')} /> : <ul aria-label={tx('Destek erişimi geçmişi')} className="space-y-3">
          {query.data.items.map(row => <li key={row.id} className="space-y-4 rounded-xl border border-border bg-surface p-4 shadow-card">
            <div className="flex flex-wrap items-center justify-between gap-2">
              <h2 className="break-words font-semibold">{row.actorDisplayName ?? tx('Bilinmiyor')}</h2>
              <StatusPill domain="supportSession" status={row.status} />
            </div>
            <p className="break-words">{row.reason}</p>
            <Facts>
              <Fact label={tx('Başlangıç')}><RelativeTime value={row.startedAt} /></Fact>
              <Fact label={tx('İzin verilen işlemler')}><PermissionBadges permissions={row.permissions} /></Fact>
            </Facts>
            <Button variant="outline" onClick={() => update({ sessionId: row.id, activityPage: '', page })}>{tx('Etkinliği görüntüle')}</Button>
            <TechnicalDetails items={sessionDetails(row)} />
          </li>)}
        </ul>}
        {query.data.totalCount > 0 && <Pagination label={tx('Destek oturumu sayfaları')} page={page} pageSize={query.data.pageSize} totalCount={query.data.totalCount} disabled={query.isFetching} onPageChange={(next) => update({ page: next })} />}
      </div>}
    </AccessQueryState>
    {selected && <SessionDetail key={selected} tenantId={tenantId} sessionId={selected} onClose={() => update({ sessionId: '', activityPage: '', page })} />}
  </div>
}

function SessionDetail({ tenantId, sessionId, onClose }: { tenantId: string; sessionId: string; onClose: () => void }) {
  useLanguage()
  const query = useSupportAccessSession(tenantId, sessionId)
  return <Section title={tx('Destek oturumu ayrıntısı')} description={tx('Bu çalışma alanındaki destek erişiminin gerekçesi ve yapılan işlemler.')} actions={<Button variant="outline" onClick={onClose}>{tx('Ayrıntıyı kapat')}</Button>}>
    <AccessQueryState isPending={query.isPending} error={query.error}>{query.data && <div className="space-y-6">
      <Facts>
        <Fact label={tx('İşlemi yapan')}>{query.data.actorDisplayName ?? tx('Bilinmiyor')}</Fact>
        <Fact label={tx('Durum')}><StatusPill domain="supportSession" status={query.data.status} /></Fact>
        <Fact label={tx('Gerekçe')}>{query.data.reason}</Fact>
        <Fact label={tx('İzin verilen işlemler')}><PermissionBadges permissions={query.data.permissions} /></Fact>
        <Fact label={tx('Başlangıç')}><RelativeTime value={query.data.startedAt} /></Fact>
      </Facts>
      <TechnicalDetails items={sessionDetails(query.data)} />
      <SessionActivity tenantId={tenantId} sessionId={sessionId} />
    </div>}</AccessQueryState>
  </Section>
}

function SessionActivity({ tenantId, sessionId }: { tenantId: string; sessionId: string }) {
  useLanguage()
  const { page, update } = useListParams('activityPage')
  const query = useSupportAccessActivity(tenantId, sessionId, { page, pageSize: PLATFORM_PAGE_SIZE })
  return <div className="space-y-3"><h3 className="font-semibold">{tx('Etkinlik geçmişi')}</h3>
    <AccessQueryState isPending={query.isPending} error={query.error}>{query.data && <>
      {query.data.items.length === 0 ? <EmptyState message={tx('Bu oturum için etkinlik kaydı yok.')} /> : <ul aria-label={tx('Etkinlik geçmişi')} className="space-y-3">
        {query.data.items.map(row => <li key={row.id} className="space-y-3 rounded-xl border border-border p-4">
          <div className="flex flex-wrap items-start justify-between gap-2">
            <h4 className="break-words font-medium">{Object.hasOwn(supportActionLabels, row.action) ? supportActionLabels[row.action] : tx('Destek işlemi')}</h4>
            <RelativeTime value={row.at} />
          </div>
          <Fact label={tx('Sonuç')}>{row.result === 'Success' ? tx('Tamamlandı') : row.result === 'Failed' || row.result === 'Failure' ? tx('Başarısız') : tx('Sonuç teknik ayrıntılarda')}</Fact>
          {(row.targetType === 'Artifact' || row.targetType === 'backup_artifact') && <Link className={`${linkClass} inline-flex min-h-11 items-center`} to={`/tenants/${tenantId}/artifacts/${encodeURIComponent(row.targetId)}`}>{tx('Yedek dosyasını görüntüle')}</Link>}
          <TechnicalDetails items={[
            { label: tx('Etkinlik kimliği'), value: row.id },
            { label: tx('İşlem kodu'), value: row.action },
            { label: tx('Hedef türü'), value: row.targetType },
            { label: tx('Hedef kimliği'), value: row.targetId },
            { label: tx('Ham sonuç'), value: row.result ?? '—' },
            { label: tx('Zaman'), value: formatDateTime(row.at) },
          ]} />
        </li>)}
      </ul>}
      {query.data.totalCount > 0 && <Pagination label={tx('Destek etkinliği sayfaları')} page={page} pageSize={query.data.pageSize} totalCount={query.data.totalCount} disabled={query.isFetching} onPageChange={(next) => update({ activityPage: next })} />}
    </>}</AccessQueryState>
  </div>
}
