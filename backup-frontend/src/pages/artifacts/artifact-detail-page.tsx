import { tx, useLanguage } from '@/lib/i18n'
import { ArtifactEncryptionSection } from '@/features/artifact-encryption/artifact-encryption-section'
import { useEffect, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { QueryState } from '@/components/data/query-state'
import { StatusPill } from '@/components/data/status-pill'
import { Button } from '@/components/ui/button'
import { buttonVariants } from '@/components/ui/button-variants'
import { Notice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { Section } from '@/components/ui/section'
import { Tabs } from '@/components/ui/tabs'
import { ConfirmDialog } from '@/features/agents/confirm-dialog'
import { ArtifactDeletedNotice, ArtifactIntegrity, ArtifactMetadata, ArtifactReplicas, ArtifactRetention, ArtifactSummary } from '@/features/artifacts/artifact-details'
import { ArtifactDownload } from '@/features/artifacts/artifact-download'
import { ArtifactVerificationSection } from '@/features/restores/verification'
import { ArtifactRestoreAction } from '@/features/artifacts/artifact-restore-action'
import { restoreIneligibility } from '@/features/artifacts/restore-eligibility'
import { artifactDate, artifactState, canCancelDeletion, deletionInProgress } from '@/features/artifacts/format'
import { ReplicationSection } from '@/features/backups/replication'
import type { ReplicationDestination } from '@/features/artifacts/types'
import { useArtifact, useArtifactActions, useReplicaRepair, useRepairStatus, useArtifactRepairs } from '@/features/artifacts/hooks'
import { useTenant } from '@/features/tenancy/use-tenant'
import { errorMessage, failureMessage } from '@/lib/api/failure-messages'

type ArtifactTab = 'summary' | 'replicas' | 'download' | 'retention' | 'metadata'
const tabs: { value: ArtifactTab; label: string }[] = [
  { value: 'summary', get label() { return tx("Özet") } }, { value: 'replicas', get label() { return tx("Replikalar") } }, { value: 'download', get label() { return tx("İndirme") } },
  { value: 'retention', get label() { return tx("Saklama ve silme") } }, { value: 'metadata', get label() { return tx("Backup metadata") } },
]

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
  return <p className="text-sm text-text">{seconds > 0 ? tx("Fiziksel silmeye kalan süre: {0} sa {1} dk {2} sn", { "0": hours, "1": minutes, "2": seconds % 60 }) : tx('Bekleme süresi doldu. Fiziksel silme işleminin başlaması bekleniyor.')} <span>{tx("En erken:")}{' '}{artifactDate(deleteAfter)}</span></p>
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
  const [tab, setTab] = useState<ArtifactTab>('summary')
  const [activeRepairId, setActiveRepairId] = useState<string | null>(null)
  const [repairingDestId, setRepairingDestId] = useState<string | null>(null)
  const [repairError, setRepairError] = useState<string | null>(null)
  const [repairDestination, setRepairDestination] = useState<ReplicationDestination | null>(null)
  const [now, setNow] = useState(() => Date.now())
  const { startRepair, cancelRepair } = useReplicaRepair(tenantId, id)
  const repairQuery = useRepairStatus(tenantId, id, activeRepairId)
  const repairsQuery = useArtifactRepairs(tenantId, id)

  const repairStatus = activeRepairId ? repairQuery.data?.status : undefined
  const repairFinished = repairStatus === 'Succeeded' || repairStatus === 'Failed' || repairStatus === 'Cancelled'
  const visibleNotice = repairStatus === 'Succeeded' ? tx('Replika başarıyla onarıldı ve doğrulandı.') : repairStatus === 'Cancelled' ? tx('Onarım işlemi iptal edildi.') : notice
  const visibleRepairError = repairError ?? (repairStatus === 'Failed' ? failureMessage(repairQuery.data?.failureCode) : null)
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
          setRepairError(errorMessage(err))
        },
      }
    )
  }

  function handleCancelRepair() {
    if (!canOperate || !activeRepairId || cancelRepair.isPending) return
    cancelRepair.mutate(activeRepairId, {
      onSuccess: () => setNotice(tx('Onarım iptal isteği gönderildi.')),
      onError: (err) => setRepairError(errorMessage(err)),
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
  return (
    <div className="space-y-6">
      <PageHeader
        crumbs={[{ label: tx('Backup dosyaları'), to: `/tenants/${tenantId}/artifacts` }, { label: artifact?.databaseName ?? tx('Backup detayları') }]}
        title={artifact?.databaseName ? tx("{0} · Backup", { "0": artifact.databaseName }) : tx('Backup detayları')}
        description={tx("Dosya bütünlüğü, saklama süresi ve replika geçmişi.")}
        meta={artifact && <>
          <StatusPill status={artifactState(artifact)} />
          {artifact.jobId && <Link className="text-sm text-primary underline-offset-4 hover:underline" to={`/tenants/${tenantId}/backup-jobs`}>{artifact.jobName || tx('Backup Job listesi')}</Link>}
          {artifact.runId && <Link className="text-sm text-primary underline-offset-4 hover:underline" to={`/tenants/${tenantId}/backup-runs/${artifact.runId}`}>{tx("Kaynak çalıştırmayı aç")}</Link>}
        </>}
        actions={artifact && <>
          {canRestore && <Link className={buttonVariants()} to={`/tenants/${tenantId}/artifacts/${artifact.id}/restore`}>{tx("Geri yükle")}</Link>}
          {canAdmin && artifact.state === 'Available' && <Button variant="outline" onClick={() => setTab('download')}>{tx("İndirmeye git")}</Button>}
          {canRequestDelete && <Button variant="destructive" disabled={busy} onClick={() => { requestDelete.reset(); cancelDelete.reset(); setDialogOpen(true) }}>{tx("Silme isteği oluştur")}</Button>}
        </>}
      />
      <QueryState isPending={query.isPending} error={query.error}>
        {artifact && <>
          {pending && <Section title={tx("Silme süreci")} className="border-warning/40">
            <div className="space-y-3">
              {artifact.deleteAfter && <DeleteCountdown deleteAfter={artifact.deleteAfter} />}
              <p className="text-sm text-text-muted">{cancellable ? tx('Fiziksel silme henüz başlamadı. Yönetici silme isteğini iptal edebilir.') : tx('Fiziksel silme başladığı için işlem artık iptal edilemez.')}</p>
              {canAdmin && cancellable && <Button variant="outline" disabled={busy} onClick={cancelDeletion}>{cancelDelete.isPending ? tx('İptal ediliyor…') : tx('Silmeyi iptal et')}</Button>}
            </div>
          </Section>}
          {!canAdmin && artifact.state !== 'Deleted' && <Notice tone="info">{tx("Silme ve silmeyi iptal etme işlemleri yalnızca yöneticilere açıktır.")}</Notice>}
          {visibleNotice && <Notice tone="success">{visibleNotice}</Notice>}
          {mutationError && !dialogOpen && <Notice tone="error">{errorMessage(mutationError)}</Notice>}
          {visibleRepairError && <Notice tone="error">{visibleRepairError}</Notice>}
          {activeRepairId && repairQuery.data && !repairFinished && <Notice tone="info" title={tx("Onarım işlemi devam ediyor")}
            action={canOperate && <Button variant="outline" size="sm" disabled={cancelRepair.isPending} onClick={handleCancelRepair}>{cancelRepair.isPending ? tx('İptal ediliyor…') : tx('Onarımı iptal et')}</Button>}>{tx("Durum:")}{' '}{repairQuery.data.status === 'InProgress' ? tx('Aktarılıyor ve doğrulanıyor') : tx('Sırada')}
            {repairQuery.data.transferredBytes > 0 && tx(" · {0} MB aktarıldı", { "0": Math.round(repairQuery.data.transferredBytes / (1024 * 1024)) })}
          </Notice>}
          <div className="space-y-6">
            <Tabs label={tx("Backup bölümleri")} idPrefix="artifact" value={tab} onChange={setTab} items={tabs} />
            <div role="tabpanel" id={`artifact-panel-${tab}`} aria-labelledby={`artifact-tab-${tab}`} className="space-y-6">
              {tab === 'summary' && <>
                <ArtifactDeletedNotice artifact={artifact} />
                <ArtifactSummary artifact={artifact} />
                {artifact.replication && <ReplicationSection replication={artifact.replication} onRepair={(destination) => { if (repairFinished) setActiveRepairId(null); setRepairError(null); setRepairDestination(destination) }} repairingDestinationId={repairFinished ? null : repairingDestId} canRepair={canRepair} repairs={repairsQuery.data} />}
                <ArtifactIntegrity artifact={artifact} />
                <ArtifactEncryptionSection encryption={artifact.encryption} />
                <ArtifactRestoreAction tenantId={tenantId} artifact={artifact} />
                <ArtifactVerificationSection tenantId={tenantId} artifact={artifact} />
              </>}
              {tab === 'replicas' && <ArtifactReplicas artifact={artifact} />}
              {tab === 'download' && <ArtifactDownload tenantId={tenantId} artifactId={id} canAdmin={canAdmin} eligible={artifact.state === 'Available'} />}
              {tab === 'retention' && <ArtifactRetention artifact={artifact} />}
              {tab === 'metadata' && <ArtifactMetadata artifact={artifact} />}
            </div>
          </div>
        </>}
      </QueryState>
      <ConfirmDialog open={dialogOpen && canRequestDelete} onClose={() => setDialogOpen(false)} title={tx("Backup için silme isteği")}
        description={tx("Bu işlem fiziksel dosyaları hemen silmez. Backup için tanımlı bekleme süresi başlar. Süre dolduğunda replikalar kalıcı olarak kaldırılır; fiziksel silme başlamadan önce isteği iptal edebilirsiniz. Geçmiş kaydı korunur.")}
        pending={requestDelete.isPending} error={requestDelete.error ? errorMessage(requestDelete.error) : undefined} confirmLabel="Bekleme süresini başlat" onConfirm={startDelete} />
      <ConfirmDialog open={Boolean(repairDestination) && canRepair} onClose={() => setRepairDestination(null)} title={tx("Replika onarılsın mı?")}
        description={<>{tx("Doğrulanmış bir Replica'dan")}{' '}<strong>{repairDestination?.storageProvider}</strong>{' '}{tx("hedefine yeni bir kopya aktarılır ve checksum doğrulanır. Depolama alanı ve aktarım kotası kullanılır. Kaynak Replica korunur.")}</>}
        pending={startRepair.isPending} error={visibleRepairError ?? undefined} confirmLabel="Onarımı başlat" onConfirm={() => { if (repairDestination) handleRepair(repairDestination) }} />
    </div>
  )
}
