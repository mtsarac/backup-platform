import { useState } from 'react'
import { DataTable, type Column } from '@/components/data/data-table'
import { Pagination } from '@/components/data/pagination'
import { StatusPill } from '@/components/data/status-pill'
import { Button } from '@/components/ui/button'
import { FilterField, Filters, filterControlClass } from '@/components/ui/filters'
import { Notice } from '@/components/ui/notice'
import { Fact, Facts, Section } from '@/components/ui/section'
import { ConfirmDialog } from '@/features/agents/confirm-dialog'
import { artifactDate } from '@/features/artifacts/format'
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
    { key: 'database', header: 'Yeni Database', wrap: true, render: item => <button className="font-medium text-primary hover:underline" onClick={() => update({ restoreId: item.id, restoreArtifact: '' })}>{item.databaseName}</button> },
    { key: 'source', header: 'Kaynak Artifact', wrap: true, render: item => item.sourceDatabaseName ?? item.artifactId },
    { key: 'target', header: 'Hedef', wrap: true, render: item => `${item.target.host}${item.target.port ? `:${item.target.port}` : ''}` },
    { key: 'status', header: 'Durum', render: item => <StatusPill status={item.status} /> },
    { key: 'progress', header: 'İlerleme', render: item => `${restoreStageLabel(item.progress.stage)} · ${restoreProgressPercent(item.progress.percent) ?? '—'}%` },
    { key: 'targetState', header: 'Hedef durumu', wrap: true, render: item => targetStateLabel(item.targetState) },
    { key: 'created', header: 'Oluşturulma', render: item => artifactDate(item.createdAt) },
  ]
  if (!scope.enabled) return <Notice>Restore Operations için etkin oturum sahipliği ve ViewBackupMetadata veya RestoreBackup izni gerekir.</Notice>
  return <div className="space-y-6">
    <Filters label="Restore filtreleri"><FilterField label="Restore durumu"><select className={filterControlClass} value={get('restoreStatus')} onChange={event => update({ restoreStatus: event.target.value })}><option value="">Tüm durumlar</option>{restoreStatuses.map(status => <option key={status} value={status}>{restoreStatusLabels[status]}</option>)}</select></FilterField></Filters>
    <SupportQueryState isPending={query.isPending} error={query.error}>{query.data && <div className="space-y-4">
      <DataTable rows={query.data.items} columns={columns} keyExtractor={item => item.id} />
      <Pagination page={page} pageSize={query.data.pageSize} totalCount={query.data.totalCount} disabled={query.isFetching} onPageChange={next => update({ restorePage: next })} />
    </div>}</SupportQueryState>
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
  return <Section title="Restore detayları">
    <div className="space-y-4">
    <SupportQueryState isPending={query.isPending} error={query.error || (!confirm && cancel.error)}>{restore && <>
      <div className="flex flex-wrap items-center justify-between gap-3"><h3 className="font-medium">{restore.databaseName} · NewDatabase</h3><StatusPill status={restore.status} /><Button variant="destructive" disabled={!mayCancel || cancel.isPending} onClick={() => { cancel.reset(); setConfirm(true) }}>Restore iptal et</Button></div>
      <p className="break-words text-sm">{restore.target.host}{restore.target.port ? `:${restore.target.port}` : ''} · Agent <code className="break-all text-xs">{restore.agentId}</code></p>
      <div role="progressbar" aria-label="Restore ilerlemesi" aria-valuemin={0} aria-valuemax={100} aria-valuenow={percent} className="h-2 overflow-hidden rounded-full bg-surface-2">{percent !== undefined && <div className="h-full bg-primary" style={{ width: `${percent}%` }} />}</div>
      <p className="text-sm">{restoreStageLabel(restore.progress.stage)} · {percent === undefined ? 'Yüzde bildirilmedi' : `%${percent}`} · {artifactDate(restore.progress.at)}</p>
      <Notice tone="warning" title="Hedef Database durumu"><p>{targetStateLabel(restore.targetState)}</p><p>{targetStateDescription(restore.targetState)}</p>{restore.result && <p>Database durumu: {databaseStateLabel(restore.result.databaseState)}</p>}{['Failed', 'Cancelled', 'AgentLost'].includes(restore.status) && <p className="font-medium">Bu işlem durumu Database'in sağlıklı olduğunu göstermez. Elle kontrol ve müdahale gerekebilir.</p>}</Notice>
      {restore.failureCode && <Notice tone="error">{restoreFailureMessage(restore.failureCode)}</Notice>}
      {restore.cancellationRequestedAt && isActiveRestore(restore.status) && <Notice>İptal istendi. Agent sonucu ve hedef Database durumu bekleniyor.</Notice>}
      <Facts columns={3}><Fact label="Oluşturulma">{artifactDate(restore.createdAt)}</Fact><Fact label="Başlangıç">{artifactDate(restore.startedAt)}</Fact><Fact label="Bitiş">{artifactDate(restore.completedAt)}</Fact></Facts>
      {restore.result && <p className="text-sm">Checksum doğrulandı: {restore.result.checksumVerified ? 'Evet' : 'Hayır'} · Backup doğrulandı: {restore.result.backupVerified ? 'Evet' : 'Hayır'}</p>}
      <h3 className="font-medium">Denemeler</h3>{restore.attempts.map(attempt => <div key={attempt.id} className="flex flex-wrap gap-3 text-sm">{attempt.number}. attempt<StatusPill status={attempt.status} /><span>{artifactDate(attempt.startedAt)} → {artifactDate(attempt.completedAt)}</span><span>{targetStateLabel(attempt.targetState)}</span>{attempt.failureCode && <span>{restoreFailureMessage(attempt.failureCode)}</span>}</div>)}
    </>}</SupportQueryState>
    <ConfirmDialog open={confirm && mayCancel} onClose={() => setConfirm(false)} title="Restore iptal edilsin mi?" description="İptal isteği Agent'a iletilir; Database motoru anında durmayabilir. Hedef Database kısmen geri yüklenmiş veya bilinmeyen durumda kalabilir. İptal, Database sağlığını garanti etmez." pending={cancel.isPending} error={cancel.error ? errorMessage(cancel.error) : undefined} onConfirm={() => { if (mayCancel && !cancel.isPending) cancel.mutate(undefined, { onSuccess: () => setConfirm(false) }) }} />
    </div>
  </Section>
}
