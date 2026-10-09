import { useEffect, useRef, useState } from 'react'
import { Dialog } from '@base-ui/react/dialog'
import { useQueryClient } from '@tanstack/react-query'
import { Link, useParams } from 'react-router-dom'
import { Button } from '@/components/ui/button'
import { ConfirmDialog } from '@/features/agents/confirm-dialog'
import { artifactDate } from '@/features/artifacts/format'
import { platformPaths } from '@/features/platform/paths'
import { inputClass } from '@/features/platform/styles'
import { useListParams } from '@/features/platform/use-list-params'
import { SupportAuditTab } from '@/features/platform/support/audit-tab'
import { SupportBackupBrowser } from '@/features/platform/support/backup-browser'
import { SupportDownloadsTab } from '@/features/platform/support/downloads-tab'
import { removeSupportData, supportKeys, useEndSupportSession, useSupportSession } from '@/features/platform/support/hooks'
import { endedMessages, supportAccess } from '@/features/platform/support/presentation'
import { SupportRestoresTab } from '@/features/platform/support/restores-tab'
import { PermissionBadges, SupportStatusPill } from '@/features/platform/support/session-table'
import { SupportQueryState } from '@/features/platform/support/support-query-state'
import { useSupportCountdown } from '@/features/platform/support/use-support-countdown'
import type { SupportSession } from '@/features/platform/support/types'
import { ApiError } from '@/lib/api/client'
import { errorMessage } from '@/lib/api/failure-messages'

export function PlatformSupportSessionPage() {
  const { sessionId = '' } = useParams()
  return <SessionDetail key={sessionId} sessionId={sessionId} />
}
function SessionDetail({ sessionId }: { sessionId: string }) {
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
  return <div className="space-y-5"><Link className="text-sm text-primary hover:underline" to={platformPaths.supportSessions}>← Support Sessions</Link><SupportQueryState isPending={query.isPending} error={mutationDenied ?? query.error}>{query.data && !mutationDenied && <SessionView session={query.data} receivedAt={query.dataUpdatedAt} />}</SupportQueryState></div>
}
function SessionView({ session, receivedAt }: { session: SupportSession; receivedAt: number }) {
  const client = useQueryClient()
  const { get, update } = useListParams()
  const countdown = useSupportCountdown(session.expiresAt, session.serverNow, receivedAt)
  const effective: SupportSession = session.status === 'Active' && countdown.seconds === 0 ? { ...session, status: 'Expired' } : session
  const access = supportAccess(effective)
  const expired = session.status === 'Active' && countdown.seconds === 0
  useEffect(() => { if (expired) { removeSupportData(client, session.id); void client.invalidateQueries({ queryKey: supportKeys.session(session.id), exact: true }) } }, [client, expired, session.id])
  const tabs = [
    { value: 'overview', label: 'Overview', enabled: true },
    { value: 'browser', label: 'Backup Browser', enabled: access.view },
    { value: 'downloads', label: 'Downloads', enabled: access.downloads },
    { value: 'restores', label: 'Restore Operations', enabled: access.restores },
    { value: 'audit', label: 'Audit', enabled: true },
  ]
  const tab = tabs.some(item => item.value === get('tab')) ? get('tab') : 'overview'
  const selected = tabs.find(item => item.value === tab)!
  return <div className="space-y-5">
    <header className="space-y-4 rounded-lg border border-border bg-surface p-5"><div className="flex flex-wrap items-start justify-between gap-4"><div><h1 className="text-2xl font-semibold text-text-heading">{effective.tenantName} · Support Session</h1><p className="mt-1 text-sm text-text-muted">{effective.actorDisplayName ?? effective.platformAdminId}</p></div><SupportStatusPill status={effective.status} /></div><PermissionBadges permissions={effective.permissions} /><p className="text-sm" aria-label="Oturum süresi">{effective.status === 'Active' ? `Kalan süre: ${countdown.label}` : 'Erişim sona erdi.'} · Süre sonu: {artifactDate(effective.expiresAt)}</p><SessionActions session={effective} /></header>
    {effective.status !== 'Active' ? <p role="status" className="rounded-lg border border-warning/30 bg-warning-subtle p-4 text-sm text-warning">{endedMessages[effective.status]} Yalnızca oturum özeti ve audit okunabilir.</p> : !effective.isOwner && <p role="status" className="rounded-lg border border-warning/30 bg-warning-subtle p-4 text-sm text-warning">Bu oturumun sahibi değilsiniz. Müşteri verileri yalnızca oturumu açan PlatformAdmin'e açıktır. Özeti ve audit kayıtlarını görebilir veya gerekçeyle erişimi geri alabilirsiniz.</p>}
    <nav role="tablist" aria-label="Destek oturumu sekmeleri" className="flex flex-wrap gap-2">{tabs.map(item => <Button key={item.value} role="tab" aria-selected={tab === item.value} aria-controls={`support-${item.value}`} disabled={!item.enabled} variant={tab === item.value ? 'default' : 'outline'} onClick={() => update({ tab: item.value })}>{item.label}</Button>)}</nav>
    <div role="tabpanel" id={`support-${tab}`} aria-label={selected.label}>
      {tab === 'overview' ? <SessionOverview session={effective} /> : tab === 'audit' ? <SupportAuditTab session={effective} /> : !selected.enabled ? <p role="status">Bu sekme için etkin oturum sahipliği ve uygun izin gerekir. Müşteri verileri yüklenmedi.</p> : tab === 'browser' ? <SupportBackupBrowser key={`${effective.id}:${effective.tenantId}:${effective.status}`} session={effective} /> : tab === 'downloads' ? <SupportDownloadsTab session={effective} /> : <SupportRestoresTab key={`${effective.id}:${effective.tenantId}:${effective.status}`} session={effective} />}
    </div>
  </div>
}
function SessionOverview({ session }: { session: SupportSession }) {
  return <section className="space-y-5 rounded-lg border border-border bg-surface p-5"><h2 className="text-lg font-semibold">Oturum özeti</h2><div className="space-y-2 rounded-lg border border-warning/30 bg-warning-subtle p-4 text-sm"><p className="font-semibold">Müşteri ortamındasınız. Bu erişim yalnızca belirtilen tenant ve süre için geçerlidir; tüm veri erişimleri denetlenir.</p><p>ViewBackupMetadata metadata okumaya, DownloadBackup dosya indirmeye, RestoreBackup yalnızca yeni Database oluşturmaya izin verir. Restore hedefleri müşterinin gerçek SQL Server ortamıdır, izole değildir.</p></div><dl className="grid gap-4 text-sm sm:grid-cols-2"><div className="sm:col-span-2"><dt className="text-text-muted">Gerekçe</dt><dd className="mt-1 whitespace-pre-wrap break-words">{session.reason}</dd></div><div className="sm:col-span-2"><dt className="mb-1 text-text-muted">İzinler</dt><dd><PermissionBadges permissions={session.permissions} /></dd></div>{[['Başlangıç', artifactDate(session.startedAt)], ['Süre sonu', artifactDate(session.expiresAt)], ['Kapanış', artifactDate(session.closedAt)], ['Kapatan / erişimi geri alan', session.closedByDisplayName ?? session.closedBy ?? '—'], ['Kapanış gerekçesi', session.closeReason ?? '—']].map(([label, value]) => <div key={label}><dt className="text-text-muted">{label}</dt><dd className="mt-1 break-words">{value}</dd></div>)}</dl></section>
}
function SessionActions({ session }: { session: SupportSession }) {
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
  return <div className="flex flex-wrap gap-3">
    {session.isOwner ? <Button variant="outline" disabled={!active || busy} onClick={() => { close.reset(); setCloseOpen(true) }}>Close session</Button> : <Button variant="destructive" disabled={!active || busy} onClick={() => { revoke.reset(); setReason(''); setInvalid(false); setRevokeOpen(true) }}>Revoke</Button>}
    <ConfirmDialog open={closeOpen && active} onClose={() => setCloseOpen(false)} title="Destek oturumu kapatılsın mı?" description="Müşteri metadata önbelleği kaldırılır; yeni download, precheck veya restore başlatılamaz. Geçmiş ve audit kayıtları korunur." confirmLabel="Oturumu kapat" pending={busy} error={close.error ? errorMessage(close.error) : undefined} onConfirm={end} />
    <Dialog.Root open={revokeOpen && active} onOpenChange={open => { if (!busy) setRevokeOpen(open) }} disablePointerDismissal={busy}><Dialog.Portal><Dialog.Backdrop className="fixed inset-0 z-40 bg-black/50" /><Dialog.Popup className="fixed left-1/2 top-1/2 z-50 w-[calc(100%-2rem)] max-w-md -translate-x-1/2 -translate-y-1/2 space-y-4 rounded-xl border border-border bg-surface p-6 shadow-xl"><Dialog.Title className="text-lg font-semibold">Destek erişimini geri al</Dialog.Title><Dialog.Description className="text-sm text-text-muted">Oturumun erişimi hemen sona erer. Gerekçe audit kaydına eklenir.</Dialog.Description><label className="block text-sm">Gerekçe<textarea className={inputClass} value={reason} maxLength={500} disabled={busy} onChange={event => setReason(event.target.value)} /></label>{invalid && <p role="alert" className="text-sm text-error">Gerekçe zorunludur ve en fazla 500 karakter olabilir.</p>}{revoke.error && <p role="alert" className="text-sm text-error">{errorMessage(revoke.error)}</p>}<div className="flex justify-end gap-2"><Dialog.Close render={<Button variant="outline" disabled={busy} />}>Vazgeç</Dialog.Close><Button variant="destructive" disabled={busy || !active} onClick={revokeAccess}>Erişimi geri al</Button></div></Dialog.Popup></Dialog.Portal></Dialog.Root>
  </div>
}
