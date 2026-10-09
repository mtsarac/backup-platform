import { useState } from 'react'
import { DataTable, type Column } from '@/components/data/data-table'
import { Pagination } from '@/components/data/pagination'
import { StatusPill } from '@/components/data/status-pill'
import { Button } from '@/components/ui/button'
import { ConfirmDialog } from '@/features/agents/confirm-dialog'
import { artifactDate } from '@/features/artifacts/format'
import { selectClass } from '@/features/platform/styles'
import { PLATFORM_PAGE_SIZE, useListParams } from '@/features/platform/use-list-params'
import { canCancelRestore, databaseStateLabel, isActiveRestore, restoreFailureMessage, restoreProgressPercent, restoreStageLabel, restoreStatuses, restoreStatusLabels, targetStateDescription, targetStateLabel } from '@/features/restores/presentation'
import type { RestoreView } from '@/features/restores/types'
import { errorMessage } from '@/lib/api/failure-messages'
import { useCancelSupportRestore, useSupportRestore, useSupportRestores } from './hooks'
import { supportAccess, supportScope } from './presentation'
import { SupportRestoreForm } from './restore-form'
import { SupportQueryState } from './support-query-state'
import type { SupportSession } from './types'

export function SupportRestoresTab({ session }: { session: SupportSession }) {
  const { page, get, update } = useListParams('restorePage')
  const scope = supportScope(session, 'restores')
  const query = useSupportRestores(scope, { page, pageSize: PLATFORM_PAGE_SIZE, status: get('restoreStatus') })
  const columns: Column<RestoreView>[] = [
    { key: 'database', header: 'Yeni Database', render: item => <button className="font-medium text-primary hover:underline" onClick={() => update({ restoreId: item.id, restoreArtifact: '' })}>{item.databaseName}</button> },
    { key: 'source', header: 'Kaynak Artifact', wrap: true, render: item => item.sourceDatabaseName ?? item.artifactId },
    { key: 'target', header: 'Hedef', render: item => `${item.target.host}${item.target.port ? `:${item.target.port}` : ''}` },
    { key: 'status', header: 'Durum', render: item => <StatusPill status={item.status} /> },
    { key: 'progress', header: 'İlerleme', render: item => `${restoreStageLabel(item.progress.stage)} · ${restoreProgressPercent(item.progress.percent) ?? '—'}%` },
    { key: 'targetState', header: 'Hedef durumu', wrap: true, render: item => targetStateLabel(item.targetState) },
    { key: 'created', header: 'Oluşturulma', render: item => artifactDate(item.createdAt) },
  ]
  if (!scope.enabled) return <p role="status">Restore Operations için etkin oturum sahipliği ve ViewBackupMetadata veya RestoreBackup izni gerekir.</p>
  return <div className="space-y-5">
    <label className="block text-sm">Restore durumu<select className={selectClass} value={get('restoreStatus')} onChange={event => update({ restoreStatus: event.target.value })}><option value="">Tüm durumlar</option>{restoreStatuses.map(status => <option key={status} value={status}>{restoreStatusLabels[status]}</option>)}</select></label>
    <SupportQueryState isPending={query.isPending} error={query.error}>{query.data && <>
      <DataTable rows={query.data.items} columns={columns} keyExtractor={item => item.id} />
      <Pagination page={page} pageSize={query.data.pageSize} totalCount={query.data.totalCount} disabled={query.isFetching} onPageChange={next => update({ restorePage: next })} />
    </>}</SupportQueryState>
    {get('restoreArtifact') && <SupportRestoreForm key={`${session.id}:${get('restoreArtifact')}`} session={session} artifactId={get('restoreArtifact')} onStarted={id => update({ restoreId: id, restoreArtifact: '' })} />}
    {get('restoreId') && <SupportRestoreDetail key={get('restoreId')} session={session} id={get('restoreId')} />}
  </div>
}
function SupportRestoreDetail({ session, id }: { session: SupportSession; id: string }) {
  const scope = supportScope(session, 'restores')
  const query = useSupportRestore(scope, id)
  const access = supportAccess(session)
  const cancel = useCancelSupportRestore(supportScope(session, 'restore'), id)
  const [confirm, setConfirm] = useState(false)
  const restore = query.data
  const mayCancel = Boolean(access.restore && restore && canCancelRestore(restore.status, restore.cancellationRequestedAt))
  const percent = restoreProgressPercent(restore?.progress.percent)
  return <section aria-label="Restore detayları" className="space-y-4 rounded-lg border border-border bg-surface p-5">
    <h2 className="text-lg font-semibold">Restore detayları</h2>
    <SupportQueryState isPending={query.isPending} error={query.error || (!confirm && cancel.error)}>{restore && <>
      <div className="flex flex-wrap items-center justify-between gap-3"><h3 className="font-medium">{restore.databaseName} · NewDatabase</h3><StatusPill status={restore.status} /><Button variant="destructive" disabled={!mayCancel || cancel.isPending} onClick={() => { cancel.reset(); setConfirm(true) }}>Restore iptal et</Button></div>
      <p className="text-sm">{restore.target.host}{restore.target.port ? `:${restore.target.port}` : ''} · Agent {restore.agentId}</p>
      <div role="progressbar" aria-label="Restore ilerlemesi" aria-valuemin={0} aria-valuemax={100} aria-valuenow={percent} className="h-2 overflow-hidden rounded-full bg-surface-2">{percent !== undefined && <div className="h-full bg-primary" style={{ width: `${percent}%` }} />}</div>
      <p className="text-sm">{restoreStageLabel(restore.progress.stage)} · {percent === undefined ? 'Yüzde bildirilmedi' : `%${percent}`} · {artifactDate(restore.progress.at)}</p>
      <section className="space-y-2 rounded-lg border border-warning/30 bg-warning-subtle p-4"><h3 className="font-medium">Hedef Database durumu</h3><p>{targetStateLabel(restore.targetState)}</p><p className="text-sm">{targetStateDescription(restore.targetState)}</p>{restore.result && <p className="text-sm">SQL Server durumu: {databaseStateLabel(restore.result.databaseState)}</p>}{['Failed', 'Cancelled', 'AgentLost'].includes(restore.status) && <p className="text-sm font-medium">Bu işlem durumu Database'in sağlıklı olduğunu göstermez. Elle kontrol ve müdahale gerekebilir.</p>}</section>
      {restore.failureCode && <p role="alert" className="text-sm text-error">{restoreFailureMessage(restore.failureCode)}</p>}
      {restore.cancellationRequestedAt && isActiveRestore(restore.status) && <p role="status" className="text-sm">İptal istendi. Agent sonucu ve hedef Database durumu bekleniyor.</p>}
      <dl className="grid gap-4 text-sm sm:grid-cols-3">{[['Oluşturulma', restore.createdAt], ['Başlangıç', restore.startedAt], ['Bitiş', restore.completedAt]].map(([label, time]) => <div key={label!}><dt className="text-text-muted">{label}</dt><dd>{artifactDate(time)}</dd></div>)}</dl>
      {restore.result && <p className="text-sm">Checksum doğrulandı: {restore.result.checksumVerified ? 'Evet' : 'Hayır'} · Backup doğrulandı: {restore.result.backupVerified ? 'Evet' : 'Hayır'}</p>}
      <h3 className="font-medium">Denemeler</h3>{restore.attempts.map(attempt => <div key={attempt.id} className="flex flex-wrap gap-3 text-sm">{attempt.number}. attempt<StatusPill status={attempt.status} /><span>{artifactDate(attempt.startedAt)} → {artifactDate(attempt.completedAt)}</span><span>{targetStateLabel(attempt.targetState)}</span>{attempt.failureCode && <span>{restoreFailureMessage(attempt.failureCode)}</span>}</div>)}
    </>}</SupportQueryState>
    <ConfirmDialog open={confirm && mayCancel} onClose={() => setConfirm(false)} title="Restore iptal edilsin mi?" description="İptal isteği Agent'a iletilir; SQL Server anında durmayabilir. Hedef Database kısmen geri yüklenmiş veya bilinmeyen durumda kalabilir. İptal, Database sağlığını garanti etmez." pending={cancel.isPending} error={cancel.error ? errorMessage(cancel.error) : undefined} onConfirm={() => { if (mayCancel && !cancel.isPending) cancel.mutate(undefined, { onSuccess: () => setConfirm(false) }) }} />
  </section>
}
