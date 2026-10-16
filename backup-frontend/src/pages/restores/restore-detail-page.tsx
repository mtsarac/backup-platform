import { useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { QueryState } from '@/components/data/query-state'
import { StatusPill } from '@/components/data/status-pill'
import { Button } from '@/components/ui/button'
import { EmptyState } from '@/components/ui/empty-state'
import { Notice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { Fact, Facts, Section } from '@/components/ui/section'
import { Tabs } from '@/components/ui/tabs'
import { ConfirmDialog } from '@/features/agents/confirm-dialog'
import { useArtifact } from '@/features/artifacts/hooks'
import { formatTimestamp } from '@/features/backups/presentation'
import { useCancelRestore, useRestore } from '@/features/restores/hooks'
import { RollbackCopyPanel, VerificationDetail } from '@/features/restores/verification-detail'
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
  const [tab, setTab] = useState<'summary' | 'details' | 'attempts' | 'checks'>('summary')
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
  return <div className="space-y-6">
    <QueryState isPending={query.isPending} error={query.error} empty={!query.isPending && !restore}>
      {restore && <>
        <PageHeader title={`${restore.purpose === 'Verification' ? 'Restore testi' : restore.purpose === 'Precheck' ? 'Geri yükleme ön kontrolü' : restore.purpose === 'Discovery' ? 'Hedef Database keşfi' : 'Geri yükleme'} · ${restore.databaseName}`}
          crumbs={[{ label: 'Geri yükleme geçmişi', to: `/tenants/${tenantId}/restores` }, { label: restore.databaseName }]}
          description={`${restoreModeLabel(restore.mode)} · ${restore.target.host}${restore.target.port ? `:${restore.target.port}` : ''}`}
          meta={<><StatusPill status={restore.status} /><span className="text-sm text-text-muted">{formatTimestamp(restore.createdAt)}</span></>}
          actions={mayCancel && <Button variant="destructive" disabled={cancel.isPending} onClick={() => { cancel.reset(); setConfirmOpen(true) }}>İptal et</Button>} />
        {notice && (isActiveRestore(restore.status) || restore.status === 'Cancelled') && <Notice>{notice}</Notice>}
        {restore.cancellationRequestedAt && isActiveRestore(restore.status) && <Notice tone="warning">İptal istendi; işlem henüz sona ermedi. Agent sonucu ve hedef Database durumu bekleniyor.</Notice>}
        {['Failed', 'Cancelled', 'AgentLost'].includes(restore.status) && restore.purpose !== 'Verification' && <section aria-label="Geri yükleme sonucu uyarısı"><Notice tone="warning" title="Hedef Database'i kontrol edin">
          <p>Cancelled, Failed veya AgentLost durumu Database'in sağlıklı olduğunu göstermez. Hedef Database durumunu kontrol edin; elle müdahale gerekebilir.</p>
          {restore.failureCode && <><p className="mt-2">{restoreFailureMessage(restore.failureCode)}</p><p className="mt-1 text-text-muted">Hata kodu: {safeRestoreCode(restore.failureCode)}</p></>}
        </Notice></section>}
        <Tabs label="Restore bölümleri" idPrefix="restore" value={tab} onChange={setTab} items={[
          { value: 'summary', label: 'Özet' }, { value: 'details', label: 'Kaynak ve seçenekler' }, { value: 'attempts', label: 'Denemeler' }, { value: 'checks', label: 'Doğrulama sonucu' },
        ]} />
        <div role="tabpanel" id={`restore-panel-${tab}`} aria-labelledby={`restore-tab-${tab}`} className="space-y-6">
          {tab === 'summary' && <>
            {restore.purpose === 'Verification' && <VerificationDetail restore={restore} />}
            <RollbackCopyPanel restore={restore} />
            {restore.purpose !== 'Verification' && <Section title="Hedef Database durumu" description={targetStateDescription(restore.targetState)}>
              <p className="font-medium text-text-heading">{targetStateLabel(restore.targetState)}</p>
              {restore.result && <p className="mt-2 text-sm">Database durumu: {databaseStateLabel(restore.result.databaseState)}</p>}
            </Section>}
            <Section title="İlerleme"><RestoreProgress stage={restore.progress.stage} percent={restore.progress.percent} at={restore.progress.at} label="Restore ilerlemesi" /></Section>
            <Section title="Restore özeti"><Facts columns={3}>
              <Fact label="Hedef Database">{restore.databaseName}</Fact>
              <Fact label="Mod">{restoreModeLabel(restore.mode)}</Fact>
              <Fact label="Deneme sayısı">{restore.attempts.length}</Fact>
              <Fact label="Oluşturulma">{formatTimestamp(restore.createdAt)}</Fact>
              <Fact label="Başlangıç">{formatTimestamp(restore.startedAt)}</Fact>
              <Fact label="Bitiş">{formatTimestamp(restore.completedAt)}</Fact>
              <Fact label="İptal isteği">{formatTimestamp(restore.cancellationRequestedAt)}</Fact>
            </Facts></Section>
          </>}
          {tab === 'details' && <Section title="Kaynak ve seçenekler"><Facts>
            <Fact label="Kaynak Artifact"><Link className="font-medium text-primary hover:underline" to={`/tenants/${tenantId}/artifacts/${restore.artifactId}`}>{restore.sourceDatabaseName ?? restore.result?.sourceBackup?.databaseName ?? artifact.data?.databaseName ?? 'Artifact ayrıntıları'}</Link>
              <p className="mt-1 break-all font-mono text-xs text-text-muted">{restore.artifactId}</p>
              {(restore.sourceJobName ?? artifact.data?.jobName) && <p className="mt-1 text-sm">{restore.sourceJobName ?? artifact.data?.jobName}</p>}
              {artifact.error && <p className="mt-1 text-sm text-text-muted">Kaynak ayrıntıları yüklenemedi. Restore kaydı kullanılabilir.</p>}
            </Fact>
            <Fact label="Agent"><Link className="break-all font-mono text-sm text-primary hover:underline" to={`/tenants/${tenantId}/agents/${restore.agentId}`}>{restore.agentId}</Link></Fact>
            <Fact label="Kaynak erişimi">{restore.sourceAccess === 'AgentLocal' ? 'Agent yerel Replica' : 'Merkezi Storage'}</Fact>
            <Fact label="Replica koruması">{restore.replicaPinned ? 'Restore için silmeye karşı korunuyor' : 'Restore koruması sona erdi'}</Fact>
            <Fact label="Checksum doğrulama isteği">{restore.options.verifyChecksum ? 'Evet' : 'Hayır'}</Fact>
            <Fact label="Backup doğrulama isteği">{restore.options.verifyBackup ? 'Evet' : 'Hayır'}</Fact>
            <Fact label="Aktif bağlantıları sonlandır">{restore.options.terminateActiveConnections ? 'Evet' : 'Hayır'}</Fact>
          </Facts></Section>}
          {tab === 'attempts' && <Section title="Denemeler">
            {restore.attempts.length === 0 ? <EmptyState title="Henüz deneme oluşturulmadı.">Agent görevi aldığında deneme bilgileri burada görünür.</EmptyState> : <ol className="divide-y divide-border">{restore.attempts.map((attempt) => <li key={attempt.id} className="py-5 first:pt-0 last:pb-0"><article aria-label={`${attempt.number}. deneme`} className="space-y-4">
              <div className="flex flex-wrap items-center justify-between gap-2"><h3 className="font-medium">{attempt.number}. deneme</h3><StatusPill status={attempt.status} /></div>
              <Facts columns={3}>{[['Oluşturulma', formatTimestamp(attempt.createdAt)], ['Agent tarafından alınma', formatTimestamp(attempt.leasedAt)], ['Başlangıç', formatTimestamp(attempt.startedAt)], ['Bitiş', formatTimestamp(attempt.completedAt)], ['Hedef Database durumu', targetStateLabel(attempt.targetState)]].map(([label, value]) => <Fact key={label} label={label}>{value}</Fact>)}</Facts>
              <RestoreProgress stage={attempt.progressStage} percent={attempt.progressPercent} at={attempt.progressAt} label={`${attempt.number}. deneme ilerlemesi`} />
              {attempt.failureCode && <Notice tone="error">{restoreFailureMessage(attempt.failureCode)} Hata kodu: {safeRestoreCode(attempt.failureCode)}</Notice>}
            </article></li>)}</ol>}
          </Section>}
          {tab === 'checks' && <Section title="Doğrulama sonucu">
            {restore.result ? <div className="space-y-4">
              <Facts><Fact label="Checksum doğrulandı">{restore.result.checksumVerified ? 'Evet' : 'Hayır'}</Fact><Fact label="Backup doğrulandı">{restore.result.backupVerified ? 'Evet' : 'Hayır'}</Fact></Facts>
              <ul className="divide-y divide-border text-sm">{restore.result.preCheck.map((check, index) => <li key={`${check.checkId}:${index}`} className="flex flex-wrap justify-between gap-2 py-3"><span className="min-w-0 break-words">{safeRestoreCode(check.checkId)} · {safeRestoreCode(check.reasonCode)}</span><span className="font-medium">{({ Passed: 'Geçti', Warning: 'Uyarı', Failed: 'Başarısız', Skipped: 'Atlandı' })[check.status]}</span></li>)}</ul>
              {restore.result.warnings.map((warning, index) => <Notice key={`${warning.code}:${index}`} tone="warning">Uyarı: {safeRestoreCode(warning.code)}</Notice>)}
            </div> : <EmptyState title="Doğrulama sonucu henüz bildirilmedi.">Agent sonucu gönderdiğinde kontrol ayrıntıları burada görünür.</EmptyState>}
          </Section>}
        </div>
      </>}
    </QueryState>
    {query.error && <Button variant="outline" onClick={() => void query.refetch()}>Tekrar dene</Button>}
    <ConfirmDialog open={confirmOpen && mayCancel} onClose={() => setConfirmOpen(false)} title="Restore iptal edilsin mi?" description="İptal isteği Agent'a iletilir; Database motorunun işlemi anında durmayabilir. Başlamış bir restore hedef Database'i kısmen geri yüklenmiş veya bilinmeyen durumda bırakabilir. Elle müdahale gerekebilir. İptal, Database'in sağlıklı olduğunu garanti etmez." pending={cancel.isPending} error={cancel.error ? restoreErrorMessage(cancel.error) : undefined} onConfirm={() => void confirmCancel()} />
  </div>
}
