import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { DataList, type Column } from '@/components/data/data-list'
import { Pagination } from '@/components/data/pagination'
import { ProgressBar } from '@/components/data/progress-bar'
import { StatusPill } from '@/components/data/status-pill'
import { Button } from '@/components/ui/button'
import { FilterField, Filters, filterControlClass } from '@/components/ui/filters'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { Section } from '@/components/ui/section'
import { TypedConfirmDialog } from '@/components/ui/confirm-dialog'
import { TechnicalDetails } from '@/components/ui/technical-details'
import { PLATFORM_PAGE_SIZE, useListParams } from '@/features/platform/use-list-params'
import { canCancelRestore, databaseStateLabel, isActiveRestore, restoreProgressPercent, restoreStageLabel, restoreStatuses, targetStateDescription, targetStateLabel } from '@/features/restores/presentation'
import type { RestoreView } from '@/features/restores/types'
import { failureMessage } from '@/lib/api/failure-messages'
import { useCancelSupportRestore, useSupportRestore, useSupportRestores } from './hooks'
import { supportAccess, supportScope } from './presentation'
import { SupportRestoreForm } from './restore-form'
import { SupportQueryState } from './support-query-state'
import type { SupportSession } from './types'
import { formatDateTime } from '@/lib/format'
import { statusLabel } from '@/lib/status'

export function SupportRestoresTab({ session }: { session: SupportSession }) {
  useLanguage()
  const { page, get, update } = useListParams('restorePage')
  const scope = supportScope(session, 'restores')
  const query = useSupportRestores(scope, { page, pageSize: PLATFORM_PAGE_SIZE, status: get('restoreStatus') })
  const columns: Column<RestoreView>[] = [
    { key: 'database', card: 'title', header: tx('Yeni veritabanı'), wrap: true, render: item => <button className="font-medium text-primary hover:underline" onClick={() => update({ restoreId: item.id, restoreArtifact: '' })}>{item.databaseName}</button> },
    { key: 'source', header: tx('Kaynak veritabanı'), wrap: true, render: item => item.sourceDatabaseName ?? '—' },
    { key: 'target', header: tx('Hedef'), wrap: true, render: item => `${item.target.host}${item.target.port ? `:${item.target.port}` : ''}` },
    { key: 'status', card: 'status', header: tx('Durum'), render: item => <StatusPill status={item.status} /> },
    { key: 'progress', header: tx('İlerleme'), render: item => `${restoreStageLabel(item.progress.stage)} · ${restoreProgressPercent(item.progress.percent) ?? '—'}%` },
    { key: 'targetState', card: 'hidden', header: tx('Hedef durumu'), wrap: true, render: item => targetStateLabel(item.targetState) },
    { key: 'created', card: 'hidden', header: tx('Oluşturulma'), render: item => formatDateTime(item.createdAt) },
  ]
  if (!scope.enabled) return <Notice>{tx("Restore Operations için etkin oturum sahipliği ve ViewBackupMetadata veya RestoreBackup izni gerekir.")}</Notice>
  return <div className="space-y-6">
    <Filters label={tx("Restore filtreleri")}><FilterField label={tx("Restore durumu")}><select className={filterControlClass} value={get('restoreStatus')} onChange={event => update({ restoreStatus: event.target.value })}><option value="">{tx("Tüm durumlar")}</option>{restoreStatuses.map(status => <option key={status} value={status}>{statusLabel('general', status)}</option>)}</select></FilterField></Filters>
    <SupportQueryState isPending={query.isPending} error={query.error}>{query.data && <div className="space-y-4">
      <DataList rows={query.data.items} columns={columns} keyExtractor={item => item.id} />
      <Pagination page={page} pageSize={query.data.pageSize} totalCount={query.data.totalCount} disabled={query.isFetching} onPageChange={next => update({ restorePage: next })} />
    </div>}</SupportQueryState>
    {get('restoreArtifact') && <SupportRestoreForm key={`${session.id}:${get('restoreArtifact')}`} session={session} artifactId={get('restoreArtifact')} onStarted={id => update({ restoreId: id, restoreArtifact: '' })} />}
    {get('restoreId') && <SupportRestoreDetail key={get('restoreId')} session={session} id={get('restoreId')} />}
  </div>
}
function SupportRestoreDetail({ session, id }: { session: SupportSession; id: string }) {
  useLanguage()
  const scope = supportScope(session, 'restores')
  const query = useSupportRestore(scope, id)
  const access = supportAccess(session)
  const cancel = useCancelSupportRestore(supportScope(session, 'restore'), id)
  const [confirm, setConfirm] = useState(false)
  const restore = query.data
  const mayCancel = Boolean(access.restore && restore && canCancelRestore(restore.status, restore.cancellationRequestedAt))
  const percent = restoreProgressPercent(restore?.progress.percent)
  return <Section title={tx("Restore detayları")}>
    <div className="space-y-4">
    <SupportQueryState isPending={query.isPending} error={query.error || (!confirm && cancel.error)}>{restore && <>
      <div className="flex flex-wrap items-center justify-between gap-3"><h3 className="font-medium">{restore.databaseName}{' '}{tx("· yeni veritabanı")}</h3><StatusPill status={restore.status} /><Button variant="destructive" disabled={!mayCancel || cancel.isPending} onClick={() => { cancel.reset(); setConfirm(true) }}>{tx("Restore iptal et")}</Button></div>
      <p className="break-words text-sm">{restore.target.host}{restore.target.port ? `:${restore.target.port}` : ''}</p>
      <ProgressBar label={tx("Restore ilerlemesi")} value={percent} />
      <p className="text-sm">{restoreStageLabel(restore.progress.stage)} · {percent === undefined ? tx('Yüzde bildirilmedi') : tx('%{0}', { '0': percent })} · {formatDateTime(restore.progress.at)}</p>
      <Notice tone="warning" title={tx("Hedef Database durumu")}><p>{targetStateLabel(restore.targetState)}</p><p>{targetStateDescription(restore.targetState)}</p>{restore.result && <p>{tx("Database durumu:")}{' '}{databaseStateLabel(restore.result.databaseState)}</p>}{['Failed', 'Cancelled', 'AgentLost'].includes(restore.status) && <p className="font-medium">{tx("Bu işlem durumu Database'in sağlıklı olduğunu göstermez. Elle kontrol ve müdahale gerekebilir.")}</p>}</Notice>
      {restore.failureCode && <ErrorNotice failureCode={restore.failureCode} scope="restore" />}
      {restore.cancellationRequestedAt && isActiveRestore(restore.status) && <Notice>{tx("İptal istendi. Agent sonucu ve hedef Database durumu bekleniyor.")}</Notice>}
      <TechnicalDetails items={[
        { label: tx('Geri yükleme kimliği'), value: restore.id },
        { label: tx('Yedekleme bilgisayarı kimliği'), value: restore.agentId },
        { label: tx('Yedek dosyası kimliği'), value: restore.artifactId },
        { label: tx('Oluşturulma'), value: formatDateTime(restore.createdAt) },
        { label: tx('Başlangıç'), value: formatDateTime(restore.startedAt) },
        { label: tx('Bitiş'), value: formatDateTime(restore.completedAt) },
      ]} />
      {restore.result && <p className="text-sm">{tx("Checksum doğrulandı:")}{' '}{restore.result.checksumVerified ? tx('Evet') : tx('Hayır')}{' '}{tx("· Backup doğrulandı:")}{' '}{restore.result.backupVerified ? tx('Evet') : tx('Hayır')}</p>}
      <h3 className="font-medium">{tx("Denemeler")}</h3>{restore.attempts.map(attempt => <div key={attempt.id} className="flex flex-wrap gap-3 text-sm">{attempt.number}{tx(". attempt")}<StatusPill status={attempt.status} /><span>{formatDateTime(attempt.startedAt)} → {formatDateTime(attempt.completedAt)}</span><span>{targetStateLabel(attempt.targetState)}</span>{attempt.failureCode && <span>{failureMessage(attempt.failureCode, 'restore')}</span>}</div>)}
    </>}</SupportQueryState>
    <TypedConfirmDialog name={restore?.databaseName ?? ''} open={confirm && mayCancel} onClose={() => setConfirm(false)} title={tx("Geri yükleme iptal edilsin mi?")}
      consequence={{
        what: tx("İptal isteği yedekleme bilgisayarına iletilir; veritabanı motoru anında durmayabilir. Hedef veritabanı kısmen geri yüklenmiş veya bilinmeyen durumda kalabilir."),
        who: tx("Hedef müşteri veritabanı etkilenir. İptal, veritabanının sağlıklı olduğunu garanti etmez."),
        reversible: tx("Hayır. Gerekirse geri yüklemeyi yeniden başlatmanız ve hedefi elle kontrol etmeniz gerekir."),
      }}
      confirmLabel={tx("İptali onayla")} pending={cancel.isPending} error={cancel.error} onConfirm={() => { if (mayCancel && !cancel.isPending) cancel.mutate(undefined, { onSuccess: () => setConfirm(false) }) }} />
    </div>
  </Section>
}
