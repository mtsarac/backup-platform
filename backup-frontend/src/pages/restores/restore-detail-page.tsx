import { useState } from 'react'
import { ArrowLeft } from 'lucide-react'
import { Link, useParams } from 'react-router-dom'
import { QueryState } from '@/components/data/query-state'
import { StatusPill } from '@/components/data/status-pill'
import { Button } from '@/components/ui/button'
import { ConfirmDialog } from '@/features/agents/confirm-dialog'
import { useArtifact } from '@/features/artifacts/hooks'
import { formatTimestamp } from '@/features/backups/presentation'
import { useCancelRestore, useRestore } from '@/features/restores/hooks'
import { canCancelRestore, databaseStateLabel, isActiveRestore, restoreErrorMessage, restoreFailureMessage, restoreModeLabel, restoreProgressPercent, restoreStageLabel, safeRestoreCode, targetStateDescription, targetStateLabel } from '@/features/restores/presentation'
import { useTenant } from '@/features/tenancy/use-tenant'

function RestoreProgress({ stage, percent: rawPercent, at, label }: { stage: string | null; percent: number | null; at: string | null; label: string }) {
  const percent = restoreProgressPercent(rawPercent)
  return <div className="space-y-2">
    <p className="text-sm">{restoreStageLabel(stage)} · {percent == null ? 'Yüzde bildirilmedi' : `%${percent.toLocaleString('tr-TR', { maximumFractionDigits: 1 })}`}</p>
    <div role="progressbar" aria-label={label} aria-valuemin={0} aria-valuemax={100} aria-valuenow={percent} className="h-2 overflow-hidden rounded-full bg-surface-2">{percent != null && <div className="h-full rounded-full bg-primary" style={{ width: `${percent}%` }} />}</div>
    <p className="text-xs text-text-muted">Son bildirim: {formatTimestamp(at)}</p>
  </div>
}
export function RestoreDetailPage() {
  const { id = '' } = useParams()
  const { tenantId } = useTenant()
  // Reset dialog/mutation state as well as query state when the route operation changes.
  return <RestoreDetail key={`${tenantId}:${id}`} id={id} />
}
function RestoreDetail({ id }: { id: string }) {
  const { tenantId, canOperate } = useTenant()
  const query = useRestore(id)
  const cancel = useCancelRestore(id)
  const restore = query.data
  const artifact = useArtifact(tenantId, restore?.artifactId ?? '')
  const [confirmOpen, setConfirmOpen] = useState(false)
  const [notice, setNotice] = useState('')
  const mayCancel = Boolean(restore && canOperate && canCancelRestore(restore.status, restore.cancellationRequestedAt))
  async function confirmCancel() {
    if (!mayCancel || cancel.isPending) return
    setNotice('')
    try {
      const response = await cancel.mutateAsync()
      setConfirmOpen(false)
      setNotice(response.status === 'Cancelled' ? 'Restore iptal edildi. Hedef Database durumunu ayrıca kontrol edin.' : 'İptal isteği iletildi; Agent sonucu bildirmeden restore tamamlanmış sayılmaz.')
    } catch {
      // The mutation error is mapped to a safe message in the confirmation dialog.
    }
  }
  return <div className="space-y-5">
    <Link to={`/tenants/${tenantId}/restores`} className="inline-flex items-center gap-1 text-sm font-medium text-primary hover:underline"><ArrowLeft className="size-4" />Geri yükleme geçmişine dön</Link>
    <QueryState isPending={query.isPending} error={query.error} empty={!query.isPending && !restore}>
      {restore && <>
        <header className="flex flex-wrap items-start justify-between gap-4">
          <div><h1 className="text-2xl font-semibold text-text-heading">{restore.purpose === 'Precheck' ? 'Geri yükleme ön kontrolü' : restore.purpose === 'Discovery' ? 'Hedef Database keşfi' : 'Geri yükleme'} · {restore.databaseName}</h1><p className="mt-1 text-sm text-text-muted">{restoreModeLabel(restore.mode)} · {restore.target.host}{restore.target.port ? `:${restore.target.port}` : ''}</p></div>
          <div className="flex items-center gap-2"><StatusPill status={restore.status} />{mayCancel && <Button variant="destructive" disabled={cancel.isPending} onClick={() => { cancel.reset(); setConfirmOpen(true) }}>İptal et</Button>}</div>
        </header>
        {notice && (isActiveRestore(restore.status) || restore.status === 'Cancelled') && <p role="status" className="rounded-lg border border-border bg-surface p-3 text-sm">{notice}</p>}
        {restore.cancellationRequestedAt && isActiveRestore(restore.status) && <p role="status" className="rounded-lg border border-border bg-surface p-4 text-sm">İptal istendi; işlem henüz sona ermedi. Agent sonucu ve hedef Database durumu bekleniyor.</p>}
        {['Failed', 'Cancelled', 'AgentLost'].includes(restore.status) && <section aria-label="Geri yükleme sonucu uyarısı" className="rounded-lg border border-error/30 bg-error-subtle p-4 text-sm"><p className="font-medium text-error">Cancelled, Failed veya AgentLost durumu Database'in sağlıklı olduğunu göstermez. Hedef Database durumunu kontrol edin; elle müdahale gerekebilir.</p>{restore.failureCode && <><p className="mt-2">{restoreFailureMessage(restore.failureCode)}</p><p className="mt-1 text-text-muted">Hata kodu: {safeRestoreCode(restore.failureCode)}</p></>}</section>}
        <section aria-labelledby="restore-target-state" className="space-y-2 rounded-lg border border-border bg-surface p-5"><h2 id="restore-target-state" className="text-lg font-semibold text-text-heading">Hedef Database durumu</h2><p className="font-medium">{targetStateLabel(restore.targetState)}</p><p className="text-sm text-text-muted">{targetStateDescription(restore.targetState)}</p>{restore.result && <p className="text-sm">SQL Server Database durumu: {databaseStateLabel(restore.result.databaseState)}</p>}</section>
        <section aria-labelledby="restore-summary" className="rounded-lg border border-border bg-surface p-5">
          <h2 id="restore-summary" className="mb-4 text-lg font-semibold text-text-heading">Restore özeti</h2>
          <dl className="grid gap-4 text-sm sm:grid-cols-2 xl:grid-cols-3">
            <div><dt className="text-text-muted">Kaynak Artifact</dt><dd className="mt-1"><Link className="text-primary hover:underline" to={`/tenants/${tenantId}/artifacts/${restore.artifactId}`}>{restore.sourceDatabaseName ?? restore.result?.sourceBackup?.databaseName ?? artifact.data?.databaseName ?? 'Artifact'} · {restore.artifactId}</Link>{(restore.sourceJobName ?? artifact.data?.jobName) && <p>{restore.sourceJobName ?? artifact.data?.jobName}</p>}{artifact.error && <p className="mt-1 text-text-muted">Kaynak ayrıntıları yüklenemedi. Restore kaydı kullanılabilir.</p>}</dd></div>
            {[
              ['Hedef Database', restore.databaseName], ['Agent', restore.agentId], ['Mod', restoreModeLabel(restore.mode)],
              ['Oluşturulma', formatTimestamp(restore.createdAt)], ['Başlangıç', formatTimestamp(restore.startedAt)], ['Bitiş', formatTimestamp(restore.completedAt)],
              ['İptal isteği', formatTimestamp(restore.cancellationRequestedAt)], ['Deneme sayısı', String(restore.attempts.length)],
              ['Kaynak erişimi', restore.sourceAccess === 'AgentLocal' ? 'Agent yerel Replica' : 'Merkezi Storage'],
              ['Replica koruması', restore.replicaPinned ? 'Restore için silmeye karşı korunuyor' : 'Restore koruması sona erdi'],
              ['Checksum doğrulama isteği', restore.options.verifyChecksum ? 'Evet' : 'Hayır'], ['Backup doğrulama isteği', restore.options.verifyBackup ? 'Evet' : 'Hayır'],
              ['Aktif bağlantıları sonlandır', restore.options.terminateActiveConnections ? 'Evet' : 'Hayır'],
            ].map(([label, value]) => <div key={label}><dt className="text-text-muted">{label}</dt><dd className="mt-1 break-words font-medium">{value}</dd></div>)}
          </dl>
        </section>
        <section aria-labelledby="restore-progress" className="space-y-4 rounded-lg border border-border bg-surface p-5"><h2 id="restore-progress" className="text-lg font-semibold text-text-heading">İlerleme</h2><RestoreProgress stage={restore.progress.stage} percent={restore.progress.percent} at={restore.progress.at} label="Restore ilerlemesi" /></section>
        <section aria-labelledby="restore-attempts" className="space-y-3"><h2 id="restore-attempts" className="text-lg font-semibold text-text-heading">Denemeler</h2>{restore.attempts.length === 0 && <p className="text-sm text-text-muted">Henüz deneme oluşturulmadı.</p>}{restore.attempts.map((attempt) => <article key={attempt.id} aria-label={`${attempt.number}. deneme`} className="space-y-4 rounded-lg border border-border bg-surface p-5">
          <div className="flex flex-wrap items-center justify-between gap-2"><h3 className="font-medium">{attempt.number}. deneme</h3><StatusPill status={attempt.status} /></div>
          <dl className="grid gap-3 text-sm sm:grid-cols-2 xl:grid-cols-4">{[['Oluşturulma', formatTimestamp(attempt.createdAt)], ['Agent tarafından alınma', formatTimestamp(attempt.leasedAt)], ['Başlangıç', formatTimestamp(attempt.startedAt)], ['Bitiş', formatTimestamp(attempt.completedAt)], ['Hedef Database durumu', targetStateLabel(attempt.targetState)]].map(([label, value]) => <div key={label}><dt className="text-text-muted">{label}</dt><dd className="mt-1">{value}</dd></div>)}</dl>
          <RestoreProgress stage={attempt.progressStage} percent={attempt.progressPercent} at={attempt.progressAt} label={`${attempt.number}. deneme ilerlemesi`} />
          {attempt.failureCode && <p className="text-sm text-error">{restoreFailureMessage(attempt.failureCode)} Hata kodu: {safeRestoreCode(attempt.failureCode)}</p>}
        </article>)}</section>
        {restore.result && <section aria-labelledby="restore-checks" className="space-y-3 rounded-lg border border-border bg-surface p-5"><h2 id="restore-checks" className="text-lg font-semibold text-text-heading">Doğrulama sonucu</h2><p className="text-sm">Checksum doğrulandı: {restore.result.checksumVerified ? 'Evet' : 'Hayır'} · Backup doğrulandı: {restore.result.backupVerified ? 'Evet' : 'Hayır'}</p><ul className="space-y-2 text-sm">{restore.result.preCheck.map((check, index) => <li key={`${check.checkId}:${index}`}>{safeRestoreCode(check.checkId)} · {({ Passed: 'Geçti', Warning: 'Uyarı', Failed: 'Başarısız', Skipped: 'Atlandı' })[check.status]} · {safeRestoreCode(check.reasonCode)}</li>)}</ul>{restore.result.warnings.map((warning, index) => <p key={`${warning.code}:${index}`} className="text-sm text-warning">Uyarı: {safeRestoreCode(warning.code)}</p>)}</section>}
      </>}
    </QueryState>
    {query.error && <Button variant="outline" onClick={() => void query.refetch()}>Tekrar dene</Button>}
    <ConfirmDialog open={confirmOpen && mayCancel} onClose={() => setConfirmOpen(false)} title="Restore iptal edilsin mi?" description="İptal isteği Agent'a iletilir; SQL Server işlemi anında durmayabilir. Başlamış bir restore hedef Database'i kısmen geri yüklenmiş veya bilinmeyen durumda bırakabilir. Eski içerik geri gelmez; elle müdahale gerekebilir. İptal, Database'in sağlıklı olduğunu garanti etmez." pending={cancel.isPending} error={cancel.error ? restoreErrorMessage(cancel.error) : undefined} onConfirm={() => void confirmCancel()} />
  </div>
}
