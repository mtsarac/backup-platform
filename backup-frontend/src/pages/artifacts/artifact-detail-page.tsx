import { useEffect, useRef, useState } from 'react'
import { Dialog } from '@base-ui/react/dialog'
import { Link, useParams } from 'react-router-dom'
import { QueryState } from '@/components/data/query-state'
import { StatusPill } from '@/components/data/status-pill'
import { Button } from '@/components/ui/button'
import { ArtifactDetails } from '@/features/artifacts/artifact-details'
import { ArtifactDownload } from '@/features/artifacts/artifact-download'
import { ArtifactRestoreAction } from '@/features/artifacts/artifact-restore-action'
import { artifactDate, artifactState, canCancelDeletion, deletionInProgress } from '@/features/artifacts/format'
import { ReplicationSection } from '@/features/backups/replication'
import type { ReplicationDestination } from '@/features/artifacts/types'
import { useArtifact, useArtifactActions, useReplicaRepair, useRepairStatus, useArtifactRepairs } from '@/features/artifacts/hooks'
import { useTenant } from '@/features/tenancy/use-tenant'
import { errorMessage } from '@/lib/api/failure-messages'

function DeleteCountdown({ deleteAfter }: { deleteAfter: string }) {
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    const timer = window.setInterval(() => setNow(Date.now()), 1000)
    return () => window.clearInterval(timer)
  }, [])
  const seconds = Math.max(0, Math.ceil((new Date(deleteAfter).getTime() - now) / 1000))
  const hours = Math.floor(seconds / 3600)
  const minutes = Math.floor((seconds % 3600) / 60)
  return <p className="text-sm text-text-muted">{seconds > 0 ? `Fiziksel silmeye kalan süre: ${hours} sa ${minutes} dk ${seconds % 60} sn` : 'Bekleme süresi doldu. Fiziksel silme işleminin başlaması bekleniyor.'} <span>En erken: {artifactDate(deleteAfter)}</span></p>
}

export function ArtifactDetailPage() {
  const { id = '' } = useParams<{ id: string }>()
  const { tenantId, canAdmin } = useTenant()
  const query = useArtifact(tenantId, id)
  const artifact = query.data
  const { requestDelete, cancelDelete } = useArtifactActions(tenantId, id)
  const [dialogOpen, setDialogOpen] = useState(false)
  const [notice, setNotice] = useState('')
  const backLink = useRef<HTMLAnchorElement>(null)
  const [activeRepairId, setActiveRepairId] = useState<string | null>(null)
  const [repairingDestId, setRepairingDestId] = useState<string | null>(null)
  const [repairError, setRepairError] = useState<string | null>(null)
  const { startRepair, cancelRepair } = useReplicaRepair(tenantId, id)
  const repairQuery = useRepairStatus(tenantId, id, activeRepairId)
  const repairsQuery = useArtifactRepairs(tenantId, id)

  useEffect(() => {
    const op = repairQuery.data
    if (!op) return
    if (op.status === 'Succeeded') {
      setNotice('Replika başarıyla onarıldı ve doğrulandı.')
      setActiveRepairId(null)
      setRepairingDestId(null)
      query.refetch()
    } else if (op.status === 'Failed') {
      setRepairError(op.failureReason || (op.failureCode ? errorMessage(new Error(op.failureCode)) : 'Onarım başarısız oldu.'))
      setActiveRepairId(null)
      setRepairingDestId(null)
    } else if (op.status === 'Cancelled') {
      setNotice('Onarım işlemi iptal edildi.')
      setActiveRepairId(null)
      setRepairingDestId(null)
    }
  }, [repairQuery.data, query])

  const canRepair = Boolean(
    artifact &&
    artifact.state !== 'Deleted' &&
    (!artifact.retentionExpiresAt || new Date(artifact.retentionExpiresAt).getTime() > Date.now() || Boolean(artifact.retentionPinnedAt)) &&
    artifact.replicas.some((r) => r.verificationState === 'Verified' && r.status === 'Available')
  )

  function handleRepair(dest: ReplicationDestination) {
    setRepairError(null)
    setNotice('')
    const destKey = dest.storageConnectionId ?? dest.storageProvider
    setRepairingDestId(destKey)
    startRepair.mutate(
      { storageConnectionId: dest.storageConnectionId, storageProvider: dest.storageProvider },
      {
        onSuccess: (op) => {
          setActiveRepairId(op.id)
          setNotice('Replika onarım işlemi başlatıldı.')
        },
        onError: (err) => {
          setRepairingDestId(null)
          setRepairError(errorMessage(err))
        },
      }
    )
  }

  function handleCancelRepair() {
    if (!activeRepairId) return
    cancelRepair.mutate(activeRepairId, {
      onSuccess: () => setNotice('Onarım iptal isteği gönderildi.'),
      onError: (err) => setRepairError(errorMessage(err)),
    })
  }
  const busy = requestDelete.isPending || cancelDelete.isPending
  const pending = artifact ? deletionInProgress(artifact) : false
  const cancellable = artifact ? canCancelDeletion(artifact) : false
  const mutationError = requestDelete.error || cancelDelete.error

  function startDelete() {
    setNotice('')
    requestDelete.mutate(undefined, { onSuccess: () => {
      setDialogOpen(false)
      setNotice('Silme isteği alındı. Bekleme süresi başladı; fiziksel dosyalar hemen silinmez.')
    } })
  }
  function cancelDeletion() {
    setNotice('')
    cancelDelete.mutate(undefined, { onSuccess: () => setNotice('Silme iptal edildi. Backup dosyaları korunuyor.') })
  }
  return (
    <div className="space-y-6">
      <Link ref={backLink} className="text-sm text-primary hover:underline" to={`/tenants/${tenantId}/artifacts`}>← Backup dosyaları</Link>
      <header className="flex flex-wrap items-center justify-between gap-4">
        <div><h1 className="text-2xl font-semibold text-text">{artifact?.databaseName ? `${artifact.databaseName} · Backup` : 'Backup detayları'}</h1><p className="mt-1 text-sm text-text-muted">Dosya bütünlüğü, saklama süresi ve replika geçmişi.</p></div>
        {artifact && <StatusPill status={artifactState(artifact)} />}
      </header>
      <QueryState isPending={query.isPending} error={query.error}>
        {artifact && <>
          <div className="flex flex-wrap items-center gap-4 text-sm">
            {artifact.jobId && <Link className="text-primary hover:underline" to={`/tenants/${tenantId}/backup-jobs`}>{artifact.jobName || 'Backup Job listesi'}</Link>}
            {artifact.runId && <Link className="text-primary hover:underline" to={`/tenants/${tenantId}/backup-runs/${artifact.runId}`}>Kaynak çalıştırmayı aç</Link>}
          </div>
          {pending && <section className="space-y-2 rounded-lg border border-amber-500/30 bg-amber-500/5 p-4" aria-label="Silme süreci">
            <h2 className="font-medium text-text">Silme süreci</h2>
            {artifact.deleteAfter && <DeleteCountdown deleteAfter={artifact.deleteAfter} />}
            <p className="text-sm text-text-muted">{cancellable ? 'Fiziksel silme henüz başlamadı. Yönetici silme isteğini iptal edebilir.' : 'Fiziksel silme başladığı için işlem artık iptal edilemez.'}</p>
            {canAdmin && cancellable && <Button variant="outline" disabled={busy} onClick={cancelDeletion}>{cancelDelete.isPending ? 'İptal ediliyor…' : 'Silmeyi iptal et'}</Button>}
          </section>}
          {canAdmin && artifact.state !== 'Deleted' && artifact.replicas.some((replica) => replica.status === 'Available') && <Dialog.Root open={dialogOpen} onOpenChange={(open) => { if (!busy) setDialogOpen(open) }}>
            <Dialog.Trigger render={<Button variant="destructive" disabled={busy} />} onClick={() => { requestDelete.reset(); cancelDelete.reset() }}>Silme isteği oluştur</Dialog.Trigger>
            <Dialog.Portal>
              <Dialog.Backdrop className="fixed inset-0 z-50 bg-black/50" />
              <Dialog.Popup finalFocus={backLink} className="fixed left-1/2 top-1/2 z-50 w-[calc(100%-2rem)] max-w-md -translate-x-1/2 -translate-y-1/2 rounded-xl border border-border bg-surface p-6 shadow-xl">
                <Dialog.Title className="text-lg font-semibold text-text">Backup için silme isteği</Dialog.Title>
                <Dialog.Description className="mt-3 text-sm leading-6 text-text-muted">Bu işlem fiziksel dosyaları hemen silmez. Backup için tanımlı bekleme süresi başlar. Süre dolduğunda replikalar kalıcı olarak kaldırılır; fiziksel silme başlamadan önce isteği iptal edebilirsiniz. Geçmiş kaydı korunur.</Dialog.Description>
                {requestDelete.error && <p role="alert" className="mt-3 text-sm text-destructive">{errorMessage(requestDelete.error)}</p>}
                <div className="mt-6 flex justify-end gap-3">
                  <Dialog.Close render={<Button variant="outline" disabled={busy} />}>Vazgeç</Dialog.Close>
                  <Button variant="destructive" disabled={busy} onClick={startDelete}>{requestDelete.isPending ? 'İstek gönderiliyor…' : 'Bekleme süresini başlat'}</Button>
                </div>
              </Dialog.Popup>
            </Dialog.Portal>
          </Dialog.Root>}
          {!canAdmin && artifact.state !== 'Deleted' && <p className="text-sm text-text-muted">Silme ve silmeyi iptal etme işlemleri yalnızca yöneticilere açıktır.</p>}
          {notice && <p role="status" className="rounded-lg border border-border bg-surface-2 p-3 text-sm text-text">{notice}</p>}
          {mutationError && !dialogOpen && <p role="alert" className="text-sm text-destructive">{errorMessage(mutationError)}</p>}
          {repairError && <p role="alert" className="text-sm text-destructive">{repairError}</p>}
          {activeRepairId && repairQuery.data && (
            <section className="flex flex-wrap items-center justify-between gap-3 rounded-lg border border-primary/30 bg-primary/5 p-4" aria-label="Onarım durumu">
              <div>
                <h2 className="font-medium text-text">Onarım işlemi devam ediyor</h2>
                <p className="mt-1 text-sm text-text-muted">
                  Durum: {repairQuery.data.status === 'InProgress' ? 'Aktarılıyor ve doğrulanıyor' : 'Sırada'}
                  {repairQuery.data.transferredBytes > 0 && ` · ${Math.round(repairQuery.data.transferredBytes / (1024 * 1024))} MB aktarıldı`}
                </p>
              </div>
              <Button variant="outline" size="sm" disabled={cancelRepair.isPending} onClick={handleCancelRepair}>
                {cancelRepair.isPending ? 'İptal ediliyor…' : 'Onarımı iptal et'}
              </Button>
            </section>
          )}
          {artifact.replication && (
            <ReplicationSection
              replication={artifact.replication}
              onRepair={handleRepair}
              repairingDestinationId={repairingDestId}
              canRepair={canRepair}
              repairs={repairsQuery.data}
            />
          )}
          <ArtifactRestoreAction tenantId={tenantId} artifact={artifact} />
          <ArtifactDownload tenantId={tenantId} artifactId={id} canAdmin={canAdmin} eligible={artifact.state === 'Available'} />
          <ArtifactDetails artifact={artifact} />
        </>}
      </QueryState>
    </div>
  )
}
