import { tx, useLanguage } from '@/lib/i18n'
import { useEffect, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { QueryState } from '@/components/data/query-state'
import { StatusPill } from '@/components/data/status-pill'
import { Button } from '@/components/ui/button'
import { buttonVariants } from '@/components/ui/button-variants'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { Section } from '@/components/ui/section'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { ArtifactDeletedNotice } from '@/features/artifacts/artifact-details'
import { ArtifactAnswers } from '@/features/artifacts/artifact-answers'
import { ArtifactTechnical } from '@/features/artifacts/artifact-technical'
import { ArtifactDownload } from '@/features/artifacts/artifact-download'
import { ArtifactVerificationSection } from '@/features/restores/verification'
import { restoreIneligibility } from '@/features/artifacts/restore-eligibility'
import { artifactName, artifactState, canCancelDeletion, deletionInProgress } from '@/features/artifacts/format'
import { ReplicationSection } from '@/features/backups/replication'
import type { ReplicationDestination } from '@/features/artifacts/types'
import { useArtifact, useArtifactActions, useReplicaRepair, useRepairStatus, useArtifactRepairs } from '@/features/artifacts/hooks'
import { useTenant } from '@/features/tenancy/use-tenant'
import { type ErrorDescription, describeError, describeFailure } from '@/lib/api/failure-messages'
import { formatDateTime } from '@/lib/format'

function DeleteCountdown({ deleteAfter }: { deleteAfter: string }) {
  useLanguage()
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    const timer = window.setInterval(() => setNow(Date.now()), 1000)
    return () => window.clearInterval(timer)
  }, [])
  const seconds = Math.max(0, Math.ceil((new Date(deleteAfter).getTime() - now) / 1000))
  const hours = Math.floor(seconds / 3600)
  const minutes = Math.floor((seconds % 3600) / 60)
  return <p className="text-sm text-text">{seconds > 0 ? tx("Fiziksel silmeye kalan süre: {0} sa {1} dk {2} sn", { "0": hours, "1": minutes, "2": seconds % 60 }) : tx('Bekleme süresi doldu. Fiziksel silme işleminin başlaması bekleniyor.')} <span>{tx("En erken:")}{' '}{formatDateTime(deleteAfter)}</span></p>
}

export function ArtifactDetailPage() {
  useLanguage()
  const { id = '' } = useParams<{ id: string }>()
  const { tenantId } = useTenant()
  return <ArtifactDetail key={`${tenantId}:${id}`} id={id} />
}

function ArtifactDetail({ id }: { id: string }) {
  useLanguage()
  const { tenantId, canAdmin, canOperate } = useTenant()
  const query = useArtifact(tenantId, id)
  const artifact = query.data
  const { requestDelete, cancelDelete } = useArtifactActions(tenantId, id)
  const [dialogOpen, setDialogOpen] = useState(false)
  const [notice, setNotice] = useState('')
  const [showDownload, setShowDownload] = useState(false)
  const [activeRepairId, setActiveRepairId] = useState<string | null>(null)
  const [repairingDestId, setRepairingDestId] = useState<string | null>(null)
  const [repairError, setRepairError] = useState<ErrorDescription | null>(null)
  const [repairDestination, setRepairDestination] = useState<ReplicationDestination | null>(null)
  const [now, setNow] = useState(() => Date.now())
  const { startRepair, cancelRepair } = useReplicaRepair(tenantId, id)
  const repairQuery = useRepairStatus(tenantId, id, activeRepairId)
  const repairsQuery = useArtifactRepairs(tenantId, id)

  const repairStatus = activeRepairId ? repairQuery.data?.status : undefined
  const repairFinished = repairStatus === 'Succeeded' || repairStatus === 'Failed' || repairStatus === 'Cancelled'
  const visibleNotice = repairStatus === 'Succeeded' ? tx('Replika başarıyla onarıldı ve doğrulandı.') : repairStatus === 'Cancelled' ? tx('Onarım işlemi iptal edildi.') : notice
  const visibleRepairError = repairError ?? (repairStatus === 'Failed' ? describeFailure(repairQuery.data?.failureCode) : null)
  const refetchArtifact = query.refetch
  useEffect(() => {
    if (repairStatus === 'Succeeded') void refetchArtifact()
  }, [repairStatus, refetchArtifact])

  const expiresAt = artifact?.retentionExpiresAt ? Date.parse(artifact.retentionExpiresAt) : null
  const pinnedAt = artifact?.retentionPinnedAt
  useEffect(() => {
    if (expiresAt === null || !Number.isFinite(expiresAt) || expiresAt <= now || pinnedAt) return
    const timer = window.setTimeout(() => setNow(Date.now()), Math.min(expiresAt - now, 2_147_483_647))
    return () => window.clearTimeout(timer)
  }, [expiresAt, now, pinnedAt])

  const canRepair = Boolean(
    canOperate &&
    artifact &&
    artifact.state !== 'Deleted' &&
    (expiresAt === null || expiresAt > now || Boolean(pinnedAt)) &&
    artifact.replicas.some((r) => r.verificationState === 'Verified' && r.status === 'Available')
  )

  function handleRepair(dest: ReplicationDestination) {
    if (!canRepair || startRepair.isPending) return
    setActiveRepairId(null)
    setRepairError(null)
    setNotice('')
    const destKey = dest.storageConnectionId ?? dest.storageProvider
    setRepairingDestId(destKey)
    startRepair.mutate(
      { storageConnectionId: dest.storageConnectionId, storageProvider: dest.storageProvider },
      {
        onSuccess: (op) => {
          setRepairDestination(null)
          setActiveRepairId(op.id)
          setNotice(tx('Replika onarım işlemi başlatıldı.'))
        },
        onError: (err) => {
          setRepairingDestId(null)
          setRepairError(describeError(err))
        },
      }
    )
  }

  function handleCancelRepair() {
    if (!canOperate || !activeRepairId || cancelRepair.isPending) return
    cancelRepair.mutate(activeRepairId, {
      onSuccess: () => setNotice(tx('Onarım iptal isteği gönderildi.')),
      onError: (err) => setRepairError(describeError(err)),
    })
  }
  const busy = requestDelete.isPending || cancelDelete.isPending
  const pending = artifact ? deletionInProgress(artifact) : false
  const cancellable = artifact ? canCancelDeletion(artifact) : false
  const mutationError = requestDelete.error || cancelDelete.error

  function startDelete() {
    if (repairFinished) setActiveRepairId(null)
    setNotice('')
    requestDelete.mutate(undefined, { onSuccess: () => {
      setDialogOpen(false)
      setNotice(tx('Silme isteği alındı. Bekleme süresi başladı; fiziksel dosyalar hemen silinmez.'))
    } })
  }
  function cancelDeletion() {
    if (repairFinished) setActiveRepairId(null)
    setNotice('')
    cancelDelete.mutate(undefined, { onSuccess: () => setNotice(tx('Silme iptal edildi. Backup dosyaları korunuyor.')) })
  }
  const canRequestDelete = Boolean(canAdmin && artifact && artifact.state !== 'Deleted' && artifact.replicas.some((replica) => replica.status === 'Available'))
  const canRestore = Boolean(canOperate && artifact && !restoreIneligibility(artifact))
  const canDownload = Boolean(canAdmin && artifact?.state === 'Available')
  return (
    <div className="space-y-6">
      <PageHeader
        crumbs={[{ label: tx('Yedek dosyaları'), to: `/tenants/${tenantId}/artifacts` }, { label: artifact?.databaseName ?? tx('Yedek dosyası') }]}
        title={artifact?.databaseName ? tx("{0} · Yedek dosyası", { "0": artifact.databaseName }) : tx('Yedek dosyası')}
        description={tx("Dosyanın sağlam olup olmadığı, geri yüklenip yüklenemeyeceği ve ne zaman silineceği.")}
        meta={artifact && <>
          <StatusPill status={artifactState(artifact)} />
          {artifact.jobId && <Link className="text-sm text-primary underline-offset-4 hover:underline" to={`/tenants/${tenantId}/backup-jobs`}>{artifact.jobName || tx('Yedekleme işleri')}</Link>}
          {artifact.runId && <Link className="text-sm text-primary underline-offset-4 hover:underline" to={`/tenants/${tenantId}/backup-runs/${artifact.runId}`}>{tx("Yedekleme denemesini aç")}</Link>}
        </>}
        actions={artifact && <>
          {canRestore && <Link className={buttonVariants()} to={`/tenants/${tenantId}/artifacts/${artifact.id}/restore`}>{tx("Geri yükle")}</Link>}
          {canDownload && <Button variant="outline" aria-expanded={showDownload} onClick={() => setShowDownload(shown => !shown)}>{tx("İndir")}</Button>}
          {canRequestDelete && <Button variant="destructive" disabled={busy} onClick={() => { requestDelete.reset(); cancelDelete.reset(); setDialogOpen(true) }}>{tx("Silme isteği oluştur")}</Button>}
        </>}
      />
      <QueryState isPending={query.isPending} error={query.error}>
        {artifact && <>
          <ArtifactDeletedNotice artifact={artifact} />
          <ArtifactAnswers artifact={artifact} canOperate={canOperate} now={now} />
          {pending && <Section title={tx("Silme süreci")} className="border-warning/40">
            <div className="space-y-3">
              {artifact.deleteAfter && <DeleteCountdown deleteAfter={artifact.deleteAfter} />}
              <p className="text-sm text-text-muted">{cancellable ? tx('Fiziksel silme henüz başlamadı. Yönetici silme isteğini iptal edebilir.') : tx('Fiziksel silme başladığı için işlem artık iptal edilemez.')}</p>
              {canAdmin && cancellable && <Button variant="outline" disabled={busy} onClick={cancelDeletion}>{cancelDelete.isPending ? tx('İptal ediliyor…') : tx('Silmeyi iptal et')}</Button>}
            </div>
          </Section>}
          {!canAdmin && artifact.state !== 'Deleted' && <Notice tone="info">{tx("Silme ve silmeyi iptal etme işlemleri yalnızca yöneticilere açıktır. Yöneticinize danışın.")}</Notice>}
          {visibleNotice && <Notice tone="success">{visibleNotice}</Notice>}
          {mutationError && !dialogOpen && <ErrorNotice error={mutationError} />}
          {visibleRepairError && <ErrorNotice title={tx("Onarım başarısız")} {...visibleRepairError} />}
          {activeRepairId && repairQuery.data && !repairFinished && <Notice tone="info" title={tx("Onarım işlemi devam ediyor")}
            action={canOperate && <Button variant="outline" size="sm" disabled={cancelRepair.isPending} onClick={handleCancelRepair}>{cancelRepair.isPending ? tx('İptal ediliyor…') : tx('Onarımı iptal et')}</Button>}>{tx("Durum:")}{' '}{repairQuery.data.status === 'InProgress' ? tx('Aktarılıyor ve doğrulanıyor') : tx('Sırada')}
            {repairQuery.data.transferredBytes > 0 && tx(" · {0} MB aktarıldı", { "0": Math.round(repairQuery.data.transferredBytes / (1024 * 1024)) })}
          </Notice>}
          {artifact.replication && <ReplicationSection replication={artifact.replication} onRepair={(destination) => { if (repairFinished) setActiveRepairId(null); setRepairError(null); setRepairDestination(destination) }} repairingDestinationId={repairFinished ? null : repairingDestId} canRepair={canRepair} repairs={repairsQuery.data} />}
          {showDownload && canDownload && <ArtifactDownload tenantId={tenantId} artifactId={id} canAdmin={canAdmin} eligible={artifact.state === 'Available'} />}
          {artifact.backupMetadata?.engine !== 'file-system' && <ArtifactVerificationSection tenantId={tenantId} artifact={artifact} />}
          <ArtifactTechnical artifact={artifact} />
        </>}
      </QueryState>
      <ConfirmDialog open={dialogOpen && canRequestDelete} onClose={() => setDialogOpen(false)} title={tx("Yedek dosyası için silme isteği")} name={artifact ? artifactName(artifact) : ''}
        consequence={{
          what: tx("Yedek dosyası hemen silinmez; bekleme süresi başlar. Süre dolunca kopyalar kalıcı olarak kaldırılır, geçmiş kaydı korunur. Kalan süre bu sayfada görünür."),
          who: tx("Bu dosyadan yapılacak indirmeler ve geri yüklemeler, bekleme süresi dolduktan sonra kullanılamaz."),
          reversible: tx("Evet, fiziksel silme başlamadan önce bu sayfadan “Silmeyi iptal et” ile vazgeçebilirsiniz. Süre dolunca silme geri alınamaz."),
        }}
        pending={requestDelete.isPending} error={requestDelete.error} confirmLabel={tx("Bekleme süresini başlat")} onConfirm={startDelete} />
      <ConfirmDialog open={Boolean(repairDestination) && canRepair} onClose={() => setRepairDestination(null)} title={tx("Replika onarılsın mı?")}
        consequence={{
          what: <>{tx("Doğrulanmış bir kopyadan")}{' '}<strong>{repairDestination?.storageProvider}</strong>{' '}{tx("hedefine yeni bir kopya aktarılır ve sağlama toplamı doğrulanır.")}</>,
          who: tx("Depolama alanı ve aktarım kotası kullanılır. Kaynak kopya korunur."),
          reversible: tx("Evet. Onarım sürerken iptal edebilirsiniz; kaynak kopyanız değişmez."),
        }}
        pending={startRepair.isPending} error={visibleRepairError} confirmLabel={tx("Onarımı başlat")} onConfirm={() => { if (repairDestination) handleRepair(repairDestination) }} />
    </div>
  )
}
