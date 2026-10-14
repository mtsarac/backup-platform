import { StatusPill } from '@/components/data/status-pill'
import { Notice } from '@/components/ui/notice'
import { Fact, Facts, Section } from '@/components/ui/section'
import { failureMessage } from '@/lib/api/failure-messages'
import { artifactDate, artifactSize, artifactState, deletionReason } from './format'
import type { BackupArtifact, BackupReplica } from './types'
import { ReplicationSummary } from '@/features/backups/replication'

/** Metadata presentation also accepts the deliberately redacted platform support projection. */
export interface ArtifactPresentation extends Omit<BackupArtifact, 'attemptId' | 'replicas'> {
  replicas: (Pick<BackupReplica, 'id' | 'storageProvider' | 'sizeBytes' | 'checksum' | 'checksumAlgorithm' | 'verificationState' | 'verificationMethod' | 'createdAt' | 'verifiedAt' | 'status' | 'usable' | 'deleteRequestedAt' | 'deleteAfter' | 'deletedAt'> & Partial<BackupReplica> & { executionLocation?: string })[]
}

export function ArtifactDeletedNotice({ artifact }: { artifact: ArtifactPresentation }) {
  if (artifact.state !== 'Deleted') return null
  return <Notice tone="info">Fiziksel dosyalar silindi. Bu kayıt yalnızca yedekleme geçmişidir; kullanılabilir bir Backup değildir.</Notice>
}

/** What this Backup is: the facts a reader needs first. */
export function ArtifactSummary({ artifact }: { artifact: ArtifactPresentation }) {
  return <Section title="Backup bilgileri" description="Bu dosyanın kaynağı, boyutu ve durumu.">
    <Facts columns={3}>
      <Fact label="Durum"><StatusPill status={artifactState(artifact)} /></Fact>
      {artifact.replication && <Fact label="Replikasyon"><ReplicationSummary replication={artifact.replication} /></Fact>}
      <Fact label="Backup tipi">{artifact.backupType ?? '—'}</Fact>
      <Fact label="Database">{artifact.databaseName || '—'}</Fact>
      <Fact label="Backup Job">{artifact.jobName || '—'}</Fact>
      <Fact label="Agent">{artifact.agentName || '—'}</Fact>
      <Fact label="Boyut">{artifactSize(artifact.sizeBytes)}</Fact>
      <Fact label="Oluşturulma">{artifactDate(artifact.createdAt)}</Fact>
      <Fact label="Kabul edilme">{artifactDate(artifact.acceptedAt)}</Fact>
    </Facts>
  </Section>
}

export function ArtifactIntegrity({ artifact }: { artifact: ArtifactPresentation }) {
  return <Section title="Bütünlük" description="Kaynak ve kayıtlı dosyanın checksum değerleri.">
    <Facts columns={1}>
      <Fact label="Checksum algoritması">{artifact.checksumAlgorithm}</Fact>
      <Fact label="Kaynak checksum"><code className="break-all font-mono text-sm">{artifact.sourceChecksum}</code></Fact>
      <Fact label="Backup checksum"><code className="break-all font-mono text-sm">{artifact.artifactChecksum}</code></Fact>
    </Facts>
  </Section>
}

export function ArtifactRetention({ artifact }: { artifact: ArtifactPresentation }) {
  return <Section title="Saklama ve silme" description="Dosyanın ne kadar saklanacağı ve silme sürecinin durumu.">
    <Facts>
      <Fact label="Saklama süresi sonu">{artifact.retentionExpiresAt ? artifactDate(artifact.retentionExpiresAt) : 'Süre sınırı yok'}</Fact>
      <Fact label="Otomatik silme koruması">{artifact.retentionPinnedAt ? `Etkin · ${artifactDate(artifact.retentionPinnedAt)}` : 'Yok'}</Fact>
      <Fact label="Fiziksel silme en erken">{artifactDate(artifact.deleteAfter)}</Fact>
      <Fact label="Silme nedeni">{deletionReason(artifact.deleteReason)}</Fact>
    </Facts>
  </Section>
}

export function ArtifactMetadata({ artifact }: { artifact: ArtifactPresentation }) {
  const meta = artifact.backupMetadata
  return <Section title="MSSQL backup metadata" description="SQL Server'ın bu backup için bildirdiği teknik bilgiler.">
    {meta ? <Facts columns={3}>
      <Fact label="Engine">{meta.engine ?? '—'}</Fact>
      <Fact label="SQL backup başladı">{artifactDate(meta.backupStartedAt)}</Fact>
      <Fact label="SQL backup tamamlandı">{artifactDate(meta.backupFinishedAt)}</Fact>
      <Fact label="SQL Server">{meta.instance?.serverName ?? '—'}</Fact>
      <Fact label="SQL Server Instance">{meta.instance?.instanceName ?? '—'}</Fact>
      <Fact label="SQL Server version">{meta.instance?.version ?? '—'}</Fact>
      <Fact label="SQL Server edition">{meta.instance?.edition ?? '—'}</Fact>
      <Fact label="SQL Server platform">{meta.instance?.hostPlatform ?? '—'}</Fact>
      <Fact label="Recovery model">{meta.sqlDetails?.recoveryModel ?? '—'}</Fact>
      <Fact label="Compatibility level">{meta.sqlDetails?.compatibilityLevel ?? '—'}</Fact>
      <Fact label="User access">{meta.sqlDetails?.userAccess ?? '—'}</Fact>
      <Fact label="Transparent data encryption">{meta.sqlDetails?.transparentDataEncryption ?? 'Bildirilmedi'}</Fact>
      <Fact label="Backup doğrulaması">{meta.verification?.state ?? '—'} · {meta.verification?.method ?? '—'}</Fact>
      <Fact label="Doğrulama başladı">{artifactDate(meta.verification?.startedAt)}</Fact>
      <Fact label="Doğrulama tamamlandı">{artifactDate(meta.verification?.finishedAt)}</Fact>
      {([['COPY_ONLY', meta.appliedOptions?.copyOnly], ['CHECKSUM', meta.appliedOptions?.checksum], ['Compression', meta.appliedOptions?.compression]] as const).map(([label, applied]) => <Fact key={label} label={label}>{applied == null ? 'Bildirilmedi' : applied ? 'Uygulandı' : 'Uygulanmadı'}</Fact>)}
    </Facts> : <p className="text-sm text-text-muted">Bu history kaydında MSSQL backup metadata bulunmuyor.</p>}
  </Section>
}

export function ArtifactReplicas({ artifact }: { artifact: ArtifactPresentation }) {
  return <section aria-label="Replikalar" className="space-y-3">
    <h2 className="text-base font-semibold text-text-heading">Replikalar <span className="font-normal text-text-muted">({artifact.replicas.length})</span></h2>
    {artifact.replicas.length === 0 && <p className="text-sm text-text-muted">Replika kaydı bulunamadı.</p>}
    {artifact.replicas.map((replica, index) => {
      const deleting = Boolean(replica.waitingForAgent || replica.deleteFailureCode || replica.deleteRequestedAt)
      return <article key={replica.id} className="space-y-4 rounded-xl border border-border bg-surface p-5 shadow-card" aria-label={`Replika ${index + 1}`}>
        <div className="flex flex-wrap items-center justify-between gap-3">
          <h3 className="font-medium text-text-heading">Replika {index + 1} · {replica.storageProvider}</h3>
          <StatusPill status={replica.status} />
        </div>
        {replica.waitingForAgent && <Notice tone="warning">Fiziksel silme için Agent bekleniyor. Agent yeniden bağlandığında silme işlemi devam edecek.</Notice>}
        {replica.status === 'Deleted' && <p className="text-sm text-text-muted">Fiziksel dosya kaldırıldı. Silinme: {artifactDate(replica.deletedAt)}</p>}
        {replica.deleteFailureCode && !replica.waitingForAgent && <Notice tone="error">Silme tamamlanamadı: {failureMessage(replica.deleteFailureCode)}</Notice>}
        <Facts columns={3}>
          {replica.storageTarget !== undefined && <Fact label="Depolama hedefi">{replica.storageTarget}</Fact>}
          <Fact label="Oluşturulma">{artifactDate(replica.createdAt)}</Fact>
          <Fact label="Boyut">{artifactSize(replica.sizeBytes)}</Fact>
          <Fact label="Kullanılabilir">{replica.status === 'Deleted' ? 'Hayır · geçmiş kaydı' : replica.usable ? 'Evet · dosya korunuyor' : 'Hayır'}</Fact>
          <Fact label="Doğrulama durumu"><StatusPill status={replica.verificationState} /></Fact>
          <Fact label="Doğrulama yöntemi">{replica.verificationMethod}</Fact>
          <Fact label="Doğrulama zamanı">{artifactDate(replica.verifiedAt)}</Fact>
        </Facts>
        <details className="rounded-lg border border-border bg-surface-2 px-4 py-3" open={deleting}>
          <summary className="min-h-6 cursor-pointer text-sm font-medium text-text-heading">Checksum ve silme ayrıntıları</summary>
          <div className="pt-4"><Facts columns={3}>
            <Fact label="Checksum"><code className="break-all font-mono text-sm">{replica.checksum}</code></Fact>
            <Fact label="Checksum algoritması">{replica.checksumAlgorithm}</Fact>
            <Fact label="Silme yürütücüsü">{(replica.executionLocation ?? replica.deletionExecutor) === 'Agent' ? 'Agent' : 'Backend'}</Fact>
            <Fact label="Silme istendi">{artifactDate(replica.deleteRequestedAt)}</Fact>
            <Fact label="Fiziksel silme en erken">{artifactDate(replica.deleteAfter)}</Fact>
            {replica.deleteNextAttemptAt !== undefined && <Fact label="Sonraki silme denemesi">{artifactDate(replica.deleteNextAttemptAt)}</Fact>}
            {replica.deleteAttempts !== undefined && <Fact label="Silme denemesi">{replica.deleteAttempts}</Fact>}
            {replica.deleteReason !== undefined && <Fact label="Silme nedeni">{deletionReason(replica.deleteReason)}</Fact>}
          </Facts></div>
        </details>
      </article>
    })}
  </section>
}

/** Everything about one Backup, stacked: used where there is no room for tabs (Backup Run attempts, platform support). */
export function ArtifactDetails({ artifact }: { artifact: ArtifactPresentation }) {
  return <div className="space-y-6">
    <ArtifactDeletedNotice artifact={artifact} />
    <ArtifactSummary artifact={artifact} />
    <ArtifactIntegrity artifact={artifact} />
    <ArtifactRetention artifact={artifact} />
    <ArtifactMetadata artifact={artifact} />
    <ArtifactReplicas artifact={artifact} />
  </div>
}
