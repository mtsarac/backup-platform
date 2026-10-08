import type { ReactNode } from 'react'
import { StatusPill } from '@/components/data/status-pill'
import { failureMessage } from '@/lib/api/failure-messages'
import { artifactDate, artifactSize, artifactState, deletionReason } from './format'
import type { BackupArtifact } from './types'

function Metadata({ label, children }: { label: string; children: ReactNode }) {
  return <div className="min-w-0"><dt className="text-sm text-text-muted">{label}</dt><dd className="mt-1 break-words text-sm text-text">{children}</dd></div>
}

export function ArtifactDetails({ artifact }: { artifact: BackupArtifact }) {
  return (
    <div className="space-y-6">
      {artifact.state === 'Deleted' && (
        <p role="status" className="rounded-lg border border-border bg-surface-2 p-4 text-sm text-text-muted">
          Fiziksel dosyalar silindi. Bu kayıt yalnızca yedekleme geçmişidir; kullanılabilir bir Backup değildir.
        </p>
      )}
      <section className="rounded-lg border border-border bg-surface p-5" aria-label="Backup bilgileri">
        <h2 className="mb-4 font-semibold text-text">Backup bilgileri</h2>
        <dl className="grid gap-5 sm:grid-cols-2 lg:grid-cols-3">
          <Metadata label="Durum"><StatusPill status={artifactState(artifact)} /></Metadata>
          <Metadata label="Backup tipi">{artifact.backupType ?? '—'}</Metadata>
          <Metadata label="Database">{artifact.databaseName || '—'}</Metadata>
          <Metadata label="Backup Job">{artifact.jobName || '—'}</Metadata>
          <Metadata label="Agent">{artifact.agentName || '—'}</Metadata>
          <Metadata label="Boyut">{artifactSize(artifact.sizeBytes)}</Metadata>
          <Metadata label="Oluşturulma">{artifactDate(artifact.createdAt)}</Metadata>
          <Metadata label="Kabul edilme">{artifactDate(artifact.acceptedAt)}</Metadata>
          <Metadata label="Saklama süresi sonu">{artifact.retentionExpiresAt ? artifactDate(artifact.retentionExpiresAt) : 'Süre sınırı yok'}</Metadata>
          <Metadata label="Otomatik silme koruması">{artifact.retentionPinnedAt ? `Etkin · ${artifactDate(artifact.retentionPinnedAt)}` : 'Yok'}</Metadata>
          <Metadata label="Fiziksel silme en erken">{artifactDate(artifact.deleteAfter)}</Metadata>
          <Metadata label="Silme nedeni">{deletionReason(artifact.deleteReason)}</Metadata>
          <Metadata label="Checksum algoritması">{artifact.checksumAlgorithm}</Metadata>
          <Metadata label="Kaynak checksum"><code className="break-all">{artifact.sourceChecksum}</code></Metadata>
          <Metadata label="Backup checksum"><code className="break-all">{artifact.artifactChecksum}</code></Metadata>
        </dl>
      </section>
      <section className="rounded-lg border border-border bg-surface p-5" aria-label="MSSQL backup metadata">
        <h2 className="mb-4 font-semibold text-text">MSSQL backup metadata</h2>
        {artifact.backupMetadata ? <dl className="grid gap-5 sm:grid-cols-2 lg:grid-cols-3">
          <Metadata label="Engine">{artifact.backupMetadata.engine ?? '—'}</Metadata>
          <Metadata label="SQL backup başladı">{artifactDate(artifact.backupMetadata.backupStartedAt)}</Metadata>
          <Metadata label="SQL backup tamamlandı">{artifactDate(artifact.backupMetadata.backupFinishedAt)}</Metadata>
          <Metadata label="SQL Server">{artifact.backupMetadata.instance?.serverName ?? '—'}</Metadata>
          <Metadata label="SQL Server Instance">{artifact.backupMetadata.instance?.instanceName ?? '—'}</Metadata>
          <Metadata label="SQL Server version">{artifact.backupMetadata.instance?.version ?? '—'}</Metadata>
          <Metadata label="SQL Server edition">{artifact.backupMetadata.instance?.edition ?? '—'}</Metadata>
          <Metadata label="SQL Server platform">{artifact.backupMetadata.instance?.hostPlatform ?? '—'}</Metadata>
          <Metadata label="Recovery model">{artifact.backupMetadata.sqlDetails?.recoveryModel ?? '—'}</Metadata>
          <Metadata label="Compatibility level">{artifact.backupMetadata.sqlDetails?.compatibilityLevel ?? '—'}</Metadata>
          <Metadata label="User access">{artifact.backupMetadata.sqlDetails?.userAccess ?? '—'}</Metadata>
          <Metadata label="Transparent data encryption">{artifact.backupMetadata.sqlDetails?.transparentDataEncryption ?? 'Bildirilmedi'}</Metadata>
          <Metadata label="Backup doğrulaması">{artifact.backupMetadata.verification?.state ?? '—'} · {artifact.backupMetadata.verification?.method ?? '—'}</Metadata>
          <Metadata label="Doğrulama başladı">{artifactDate(artifact.backupMetadata.verification?.startedAt)}</Metadata>
          <Metadata label="Doğrulama tamamlandı">{artifactDate(artifact.backupMetadata.verification?.finishedAt)}</Metadata>
          {([['COPY_ONLY', artifact.backupMetadata.appliedOptions?.copyOnly], ['CHECKSUM', artifact.backupMetadata.appliedOptions?.checksum], ['Compression', artifact.backupMetadata.appliedOptions?.compression]] as const).map(([label, applied]) => <Metadata key={label} label={label}>{applied == null ? 'Bildirilmedi' : applied ? 'Uygulandı' : 'Uygulanmadı'}</Metadata>)}
        </dl> : <p className="text-sm text-text-muted">Bu history kaydında MSSQL backup metadata bulunmuyor.</p>}
      </section>
      <section aria-label="Replikalar" className="space-y-3">
        <h2 className="font-semibold text-text">Replikalar <span className="text-text-muted">({artifact.replicas.length})</span></h2>
        {artifact.replicas.length === 0 && <p className="text-sm text-text-muted">Replika kaydı bulunamadı.</p>}
        {artifact.replicas.map((replica, index) => (
          <article key={replica.id} className="rounded-lg border border-border bg-surface p-5" aria-label={`Replika ${index + 1}`}>
            <div className="mb-4 flex flex-wrap items-center justify-between gap-3">
              <h3 className="font-medium text-text">Replika {index + 1} · {replica.storageProvider}</h3>
              <StatusPill status={replica.status} />
            </div>
            {replica.waitingForAgent && <p role="status" className="mb-4 rounded-md bg-amber-500/10 p-3 text-sm text-amber-700 dark:text-amber-300">Fiziksel silme için Agent bekleniyor. Agent yeniden bağlandığında silme işlemi devam edecek.</p>}
            {replica.status === 'Deleted' && <p className="mb-4 text-sm text-text-muted">Fiziksel dosya kaldırıldı. Silinme: {artifactDate(replica.deletedAt)}</p>}
            {replica.deleteFailureCode && !replica.waitingForAgent && <p role="status" className="mb-4 rounded-md bg-destructive/10 p-3 text-sm text-destructive">Silme tamamlanamadı: {failureMessage(replica.deleteFailureCode)}</p>}
            <dl className="grid gap-5 sm:grid-cols-2 lg:grid-cols-3">
              <Metadata label="Depolama hedefi">{replica.storageTarget}</Metadata>
              <Metadata label="Oluşturulma">{artifactDate(replica.createdAt)}</Metadata>
              <Metadata label="Boyut">{artifactSize(replica.sizeBytes)}</Metadata>
              <Metadata label="Kullanılabilir">{replica.status === 'Deleted' ? 'Hayır · geçmiş kaydı' : replica.usable ? 'Evet · dosya korunuyor' : 'Hayır'}</Metadata>
              <Metadata label="Doğrulama durumu"><StatusPill status={replica.verificationState} /></Metadata>
              <Metadata label="Doğrulama yöntemi">{replica.verificationMethod}</Metadata>
              <Metadata label="Doğrulama zamanı">{artifactDate(replica.verifiedAt)}</Metadata>
              <Metadata label="Checksum"><code className="break-all">{replica.checksum}</code></Metadata>
              <Metadata label="Checksum algoritması">{replica.checksumAlgorithm}</Metadata>
              <Metadata label="Silme yürütücüsü">{replica.deletionExecutor === 'Agent' ? 'Agent' : 'Backend'}</Metadata>
              <Metadata label="Silme istendi">{artifactDate(replica.deleteRequestedAt)}</Metadata>
              <Metadata label="Fiziksel silme en erken">{artifactDate(replica.deleteAfter)}</Metadata>
              <Metadata label="Sonraki silme denemesi">{artifactDate(replica.deleteNextAttemptAt)}</Metadata>
              <Metadata label="Silme denemesi">{replica.deleteAttempts}</Metadata>
              <Metadata label="Silme nedeni">{deletionReason(replica.deleteReason)}</Metadata>
            </dl>
          </article>
        ))}
      </section>
    </div>
  )
}
