import { tx, useLanguage } from '@/lib/i18n'
import { useEffect, useRef, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { useParams } from 'react-router-dom'
import { Button } from '@/components/ui/button'
import { Field } from '@/components/ui/field'
import { Notice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { Fact, Facts, Section } from '@/components/ui/section'
import { TechnicalDetails } from '@/components/ui/technical-details'
import { Tabs } from '@/components/ui/tabs'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { platformPaths } from '@/features/platform/paths'
import { FormDialog } from '@/components/ui/form-dialog'
import { inputClass } from '@/components/ui/form-styles'
import { useListParams } from '@/features/platform/use-list-params'
import { SupportAuditTab } from '@/features/platform/support/audit-tab'
import { SupportBackupBrowser } from '@/features/platform/support/backup-browser'
import { SupportDownloadsTab } from '@/features/platform/support/downloads-tab'
import { removeSupportData, supportKeys, useEndSupportSession, useSupportSession } from '@/features/platform/support/hooks'
import { endedMessages, supportAccess, supportSessionDetails } from '@/features/platform/support/presentation'
import { SupportRestoresTab } from '@/features/platform/support/restores-tab'
import { PermissionBadges } from '@/features/platform/support/session-table'
import { SupportQueryState } from '@/features/platform/support/support-query-state'
import { useSupportCountdown } from '@/features/platform/support/use-support-countdown'
import type { SupportSession } from '@/features/platform/support/types'
import { ApiError } from '@/lib/api/client'
import { RelativeTime } from '@/components/data/relative-time'
import { StatusPill } from '@/components/data/status-pill'

const crumbs = [{ label: 'Platform', to: platformPaths.root }, { get label() { return tx("Support Sessions") }, to: platformPaths.supportSessions }]

export function PlatformSupportSessionPage() {
  useLanguage()
  const { sessionId = '' } = useParams()
  return <SessionDetail key={sessionId} sessionId={sessionId} />
}
function SessionDetail({ sessionId }: { sessionId: string }) {
  useLanguage()
  const client = useQueryClient()
  const query = useSupportSession(sessionId)
  const [mutationDenied, setMutationDenied] = useState<ApiError | null>(null)
  useEffect(() => { if (mutationDenied) removeSupportData(client, sessionId) }, [client, mutationDenied, sessionId])
  useEffect(() => {
    function denied(error: unknown, mutation = false) {
      if (error instanceof ApiError && error.status === 403 && error.code === 'support_access_denied') {
        if (mutation) setMutationDenied(error)
        void client.invalidateQueries({ queryKey: supportKeys.session(sessionId), exact: true })
      }
    }
    const querySubscription = client.getQueryCache().subscribe(event => { if (event.type === 'updated' && event.action.type === 'error' && event.query.queryKey[0] === 'platform' && event.query.queryKey[1] === 'support' && event.query.queryKey[3] === sessionId && event.query.queryKey[4] === 'tenant') denied(event.query.state.error) })
    const mutationSubscription = client.getMutationCache().subscribe(event => { if (event.type === 'updated' && event.action.type === 'error' && event.mutation.options.mutationKey?.[3] === sessionId) denied(event.mutation.state.error, true) })
    return () => { querySubscription(); mutationSubscription(); removeSupportData(client, sessionId) }
  }, [client, sessionId])
  const error = mutationDenied ?? query.error
  if (query.data && !error) return <SessionView session={query.data} receivedAt={query.dataUpdatedAt} />
  return <div className="space-y-6"><PageHeader crumbs={crumbs} title={tx("Support Session")} /><SupportQueryState isPending={query.isPending} error={error} /></div>
}
function SessionView({ session, receivedAt }: { session: SupportSession; receivedAt: number }) {
  useLanguage()
  const client = useQueryClient()
  const { get, update } = useListParams()
  const countdown = useSupportCountdown(session.expiresAt, session.serverNow, receivedAt)
  const effective: SupportSession = session.status === 'Active' && countdown.seconds === 0 ? { ...session, status: 'Expired' } : session
  const access = supportAccess(effective)
  const expired = session.status === 'Active' && countdown.seconds === 0
  useEffect(() => { if (expired) { removeSupportData(client, session.id); void client.invalidateQueries({ queryKey: supportKeys.session(session.id), exact: true }) } }, [client, expired, session.id])
  // Only tabs the session's permissions open are offered; an unheld one in the URL falls back to the overview.
  const tabs = [
    { value: 'overview', label: tx('Overview'), enabled: true },
    { value: 'browser', label: tx('Backup Browser'), enabled: access.view },
    { value: 'downloads', label: tx('Downloads'), enabled: access.downloads },
    { value: 'restores', label: tx('Restore Operations'), enabled: access.restores },
    { value: 'audit', label: tx('Audit'), enabled: true },
  ].filter(item => item.enabled)
  const tab = tabs.some(item => item.value === get('tab')) ? get('tab') : 'overview'
  const active = effective.status === 'Active'
  return <div className="space-y-6">
    <PageHeader crumbs={crumbs} title={tx("{0} · Support Session", { "0": effective.tenantName })} description={tx("Oturumu açan: {0}", { "0": effective.actorDisplayName ?? effective.platformAdminId })}
      meta={<><StatusPill domain="supportSession" status={effective.status} /><span aria-label={tx("Oturum süresi")} className="text-sm text-text-muted">{active ? tx("Kalan süre: {0}", { "0": countdown.label }) : tx('Erişim sona erdi.')}{' '}{tx("· Süre sonu:")}{' '}<RelativeTime value={effective.expiresAt} /></span></>}
      actions={<SessionActions session={effective} />} />
    {effective.status !== 'Active' ? <Notice tone="warning">{endedMessages[effective.status]}{' '}{tx("Yalnızca oturum özeti ve audit okunabilir.")}</Notice>
      : !effective.isOwner && <Notice tone="warning">{tx("Bu oturumun sahibi değilsiniz. Müşteri verileri yalnızca oturumu açan PlatformAdmin'e açıktır. Özeti ve audit kayıtlarını görebilir veya gerekçeyle erişimi geri alabilirsiniz.")}</Notice>}
    <div>
      <Tabs label={tx("Destek oturumu sekmeleri")} idPrefix="support" value={tab} onChange={value => update({ tab: value })} items={tabs} />
      <div role="tabpanel" id={`support-panel-${tab}`} aria-labelledby={`support-tab-${tab}`} className="pt-6">
        {tab === 'overview' ? <SessionOverview session={effective} /> : tab === 'audit' ? <SupportAuditTab session={effective} /> : tab === 'browser' ? <SupportBackupBrowser key={`${effective.id}:${effective.tenantId}:${effective.status}`} session={effective} /> : tab === 'downloads' ? <SupportDownloadsTab session={effective} /> : <SupportRestoresTab key={`${effective.id}:${effective.tenantId}:${effective.status}`} session={effective} />}
      </div>
    </div>
  </div>
}
function SessionOverview({ session }: { session: SupportSession }) {
  useLanguage()
  return <div className="space-y-5">
    <Notice tone="warning" title={tx("Müşteri ortamındasınız")}>
      <p>{tx("Bu erişim yalnızca belirtilen tenant ve süre için geçerlidir; tüm veri erişimleri denetlenir.")}</p>
      <p>{tx("ViewBackupMetadata metadata okumaya, DownloadBackup dosya indirmeye, RestoreBackup yalnızca yeni Database oluşturmaya izin verir. Restore hedefleri müşterinin gerçek veritabanı sunucusudur, izole değildir.")}</p>
    </Notice>
    <Section title={tx("Oturum özeti")}>
      <div className="space-y-4">
        <Facts columns={1}>
          <Fact label={tx("Gerekçe")}><span className="whitespace-pre-wrap">{session.reason}</span></Fact>
          <Fact label={tx("İzinler")}><PermissionBadges permissions={session.permissions} /></Fact>
        </Facts>
        {session.closedAt && <Facts>
          <Fact label={tx("Kapatan / erişimi geri alan")}>{session.closedByDisplayName ?? session.closedBy ?? '—'}</Fact>
          <Fact label={tx("Kapanış gerekçesi")}>{session.closeReason ?? '—'}</Fact>
        </Facts>}
        <TechnicalDetails items={supportSessionDetails(session, { plainView: true })} />
      </div>
    </Section>
  </div>
}
function SessionActions({ session }: { session: SupportSession }) {
  useLanguage()
  const { close, revoke } = useEndSupportSession(session.id)
  const [closeOpen, setCloseOpen] = useState(false)
  const [revokeOpen, setRevokeOpen] = useState(false)
  const [reason, setReason] = useState('')
  const [invalid, setInvalid] = useState(false)
  const submitting = useRef(false)
  const active = session.status === 'Active'
  const busy = close.isPending || revoke.isPending
  function end() { if (!active || !session.isOwner || submitting.current) return; submitting.current = true; close.mutate(undefined, { onSuccess: () => setCloseOpen(false), onSettled: () => { submitting.current = false } }) }
  function revokeAccess() {
    if (!active || session.isOwner || submitting.current) return
    const value = reason.trim()
    if (!value || value.length > 500) { setInvalid(true); return }
    setInvalid(false); submitting.current = true
    revoke.mutate(value, { onSuccess: () => setRevokeOpen(false), onSettled: () => { submitting.current = false } })
  }
  return <>
    {session.isOwner ? <Button variant="outline" disabled={!active || busy} onClick={() => { close.reset(); setCloseOpen(true) }}>{tx("Close session")}</Button> : <Button variant="destructive" disabled={!active || busy} onClick={() => { revoke.reset(); setReason(''); setInvalid(false); setRevokeOpen(true) }}>{tx("Revoke")}</Button>}
    <ConfirmDialog open={closeOpen && active} onClose={() => setCloseOpen(false)} title={tx("Destek oturumu kapatılsın mı?")}
      consequence={{
        what: tx("Müşteri üst veri önbelleği kaldırılır; yeni indirme, ön kontrol veya geri yükleme başlatılamaz."),
        who: tx("Yalnızca bu destek oturumu kapanır; geçmiş ve denetim kayıtları korunur."),
        reversible: tx("Hayır. Gerekirse yeni bir destek oturumu açmanız gerekir."),
      }}
      confirmLabel={tx("Oturumu kapat")} pending={busy} error={close.error} onConfirm={end} />
    {revokeOpen && active && <FormDialog title={tx("Destek erişimini geri al")} description={tx("Oturumun erişimi hemen sona erer. Gerekçe audit kaydına eklenir.")} submitLabel={tx("Erişimi geri al")} pendingLabel={tx("Erişim geri alınıyor…")} pending={busy}
      error={revoke.error} onSubmit={revokeAccess} onClose={() => setRevokeOpen(false)}>
      <Field label={tx("Gerekçe")} hint={tx("Zorunlu; en fazla 500 karakter.")} error={invalid ? tx('Gerekçe zorunludur ve en fazla 500 karakter olabilir.') : undefined}>
        {control => <textarea {...control} className={inputClass} rows={3} value={reason} maxLength={500} disabled={busy} onChange={event => setReason(event.target.value)} />}
      </Field>
    </FormDialog>}
  </>
}
