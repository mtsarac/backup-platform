import { tx, useLanguage } from '@/lib/i18n'
import { StatusPill } from '@/components/data/status-pill'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { Fact, Facts, Section } from '@/components/ui/section'
import { describeFailure } from '@/lib/api/failure-messages'
import { artifactState, deletionReason } from './format'
import type { BackupArtifact, BackupReplica } from './types'
import { ReplicationSummary } from '@/features/backups/replication'
import { ArtifactEncryptionSection } from '@/features/artifact-encryption/artifact-encryption-section'
import { formatBytes, formatDateTime } from '@/lib/format'

/** Metadata presentation also accepts the deliberately redacted platform support projection. */
export interface ArtifactPresentation extends Omit<BackupArtifact, 'attemptId' | 'replicas'> {
  replicas: (Pick<BackupReplica, 'id' | 'storageProvider' | 'sizeBytes' | 'checksum' | 'checksumAlgorithm' | 'verificationState' | 'verificationMethod' | 'createdAt' | 'verifiedAt' | 'status' | 'usable' | 'deleteRequestedAt' | 'deleteAfter' | 'deletedAt'> & Partial<BackupReplica> & { executionLocation?: string })[]
}

export function ArtifactDeletedNotice({ artifact }: { artifact: ArtifactPresentation }) {
  useLanguage()
  if (artifact.state !== 'Deleted') return null
  return <Notice tone="info">{tx("Fiziksel dosyalar silindi. Bu kayıt yalnızca yedekleme geçmişidir; kullanılabilir bir Backup değildir.")}</Notice>
}

/** What this Backup is: the facts a reader needs first. */
export function ArtifactSummary({ artifact }: { artifact: ArtifactPresentation }) {
  useLanguage()
  return <Section title={tx("Backup bilgileri")} description={tx("Bu dosyanın kaynağı, boyutu ve durumu.")}>
    <Facts columns={3}>
      <Fact label={tx("Durum")}><StatusPill status={artifactState(artifact)} /></Fact>
      {artifact.replication && <Fact label={tx("Replikasyon")}><ReplicationSummary replication={artifact.replication} /></Fact>}
      <Fact label={tx("Backup tipi")}>{artifact.backupType ?? '—'}</Fact>
      <Fact label={tx("Database")}>{artifact.databaseName || '—'}</Fact>
      <Fact label={tx("Backup Job")}>{artifact.jobName || '—'}</Fact>
      <Fact label={tx("Agent")}>{artifact.agentName || '—'}</Fact>
      <Fact label={tx("Boyut")}>{formatBytes(artifact.sizeBytes)}</Fact>
      <Fact label={tx("Oluşturulma")}>{formatDateTime(artifact.createdAt)}</Fact>
      <Fact label={tx("Kabul edilme")}>{formatDateTime(artifact.acceptedAt)}</Fact>
    </Facts>
  </Section>
}

export function ArtifactIntegrity({ artifact }: { artifact: ArtifactPresentation }) {
  useLanguage()
  return <Section title={tx("Bütünlük")} description={tx("Kaynak ve kayıtlı dosyanın checksum değerleri.")}>
    <Facts columns={1}>
      <Fact label={tx("Checksum algoritması")}>{artifact.checksumAlgorithm}</Fact>
      <Fact label={tx("Kaynak checksum")}><code className="break-all font-mono text-sm">{artifact.sourceChecksum}</code></Fact>
      <Fact label={tx("Backup checksum")}><code className="break-all font-mono text-sm">{artifact.artifactChecksum}</code></Fact>
    </Facts>
  </Section>
}

export function ArtifactRetention({ artifact }: { artifact: ArtifactPresentation }) {
  useLanguage()
  return <Section title={tx("Saklama ve silme")} description={tx("Dosyanın ne kadar saklanacağı ve silme sürecinin durumu.")}>
    <Facts>
      <Fact label={tx("Saklama süresi sonu")}>{artifact.retentionExpiresAt ? formatDateTime(artifact.retentionExpiresAt) : tx('Süre sınırı yok')}</Fact>
      <Fact label={tx("Otomatik silme koruması")}>{artifact.retentionPinnedAt ? tx("Etkin · {0}", { "0": formatDateTime(artifact.retentionPinnedAt) }) : tx('Yok')}</Fact>
      <Fact label={tx("Fiziksel silme en erken")}>{formatDateTime(artifact.deleteAfter)}</Fact>
      <Fact label={tx("Silme nedeni")}>{deletionReason(artifact.deleteReason)}</Fact>
    </Facts>
  </Section>
}

export function ArtifactMetadata({ artifact }: { artifact: ArtifactPresentation }) {
  useLanguage()
  const meta = artifact.backupMetadata
  const isPg = meta?.engine === 'postgres'
  const isFiles = meta?.engine === 'file-system'
  return <Section title={isFiles ? tx('Dosya arşivi bilgileri') : isPg ? tx('PostgreSQL backup metadata') : tx('Database backup metadata')} description={isFiles ? tx('Dosya içeriği, izinler ve zaman damgaları SHA-256 manifestiyle korunur.') : isPg ? tx('pg_dump custom-format mantıksal Full Backup bilgileri.') : tx('Database motorunun bu backup için bildirdiği teknik bilgiler.')}>
    {meta ? <Facts columns={3}>
      <Fact label={tx("Engine")}>{meta.engine ?? '—'}</Fact>
      <Fact label={tx("Backup başladı")}>{formatDateTime(meta.backupStartedAt)}</Fact>
      <Fact label={tx("Backup tamamlandı")}>{formatDateTime(meta.backupFinishedAt)}</Fact>
      <Fact label={tx("Sunucu")}>{meta.instance?.serverName ?? '—'}</Fact>
      {!isPg && !isFiles && <Fact label={tx("SQL Server Instance")}>{meta.instance?.instanceName ?? '—'}</Fact>}
      <Fact label={tx("Sunucu sürümü")}>{meta.instance?.version ?? '—'}</Fact>
      {!isPg && !isFiles && <>
        <Fact label={tx("SQL Server edition")}>{meta.instance?.edition ?? '—'}</Fact>
        <Fact label={tx("SQL Server platform")}>{meta.instance?.hostPlatform ?? '—'}</Fact>
        <Fact label={tx("Recovery model")}>{meta.sqlDetails?.recoveryModel ?? '—'}</Fact>
        <Fact label={tx("Compatibility level")}>{meta.sqlDetails?.compatibilityLevel ?? '—'}</Fact>
        <Fact label={tx("User access")}>{meta.sqlDetails?.userAccess ?? '—'}</Fact>
        <Fact label={tx("Transparent data encryption")}>{meta.sqlDetails?.transparentDataEncryption ?? tx('Bildirilmedi')}</Fact>
      </>}
      {isPg && <>
        <Fact label={tx("Arşiv biçimi")}>{meta.postgreSqlDetails?.archiveFormat ?? 'custom'}{' '}{tx("· logical Full")}</Fact>
        <Fact label={tx("pg_dump sürümü")}>{meta.postgreSqlDetails?.clientVersion ?? '—'}</Fact>
        <Fact label={tx("Encoding")}>{meta.postgreSqlDetails?.encoding ?? '—'}</Fact>
        <Fact label={tx("Collation")}>{meta.postgreSqlDetails?.collation ?? '—'}</Fact>
        <Fact label={tx("Sunucu data_checksums")}>{meta.postgreSqlDetails?.dataChecksums ?? '—'}</Fact>
        <Fact label={tx("Kaynak Database boyutu (byte)")}>{meta.postgreSqlDetails?.databaseSizeBytes ?? '—'}</Fact>
        <Fact label={tx("Extensions")}>{meta.postgreSqlDetails?.extensions || '—'}</Fact>
      </>}
      <Fact label={tx("Backup doğrulaması")}>{meta.verification?.state ?? '—'} · {meta.verification?.method ?? '—'}</Fact>
      <Fact label={tx("Doğrulama başladı")}>{formatDateTime(meta.verification?.startedAt)}</Fact>
      <Fact label={tx("Doğrulama tamamlandı")}>{formatDateTime(meta.verification?.finishedAt)}</Fact>
      {!isPg && !isFiles && <Fact label={tx("COPY_ONLY")}>{meta.appliedOptions?.copyOnly == null ? tx('Bildirilmedi') : meta.appliedOptions.copyOnly ? tx('Uygulandı') : tx('Uygulanmadı')}</Fact>}
      {([[isFiles ? 'SHA-256' : isPg ? 'data_checksums' : 'CHECKSUM', meta.appliedOptions?.checksum], ['Compression', meta.appliedOptions?.compression]] as const).map(([label, applied]) => <Fact key={label} label={label}>{applied == null ? tx('Bildirilmedi') : applied ? tx('Uygulandı') : tx('Uygulanmadı')}</Fact>)}
    </Facts> : <p className="text-sm text-text-muted">{tx("Bu history kaydında Database backup metadata bulunmuyor.")}</p>}
  </Section>
}

export function ArtifactReplicas({ artifact }: { artifact: ArtifactPresentation }) {
  useLanguage()
  return <section aria-label={tx("Replikalar")} className="space-y-3">
    <h2 className="text-base font-semibold text-text-heading">{tx("Replikalar")}{' '}<span className="font-normal text-text-muted">({artifact.replicas.length})</span></h2>
    {artifact.replicas.length === 0 && <p className="text-sm text-text-muted">{tx("Replika kaydı bulunamadı.")}</p>}
    {artifact.replicas.map((replica, index) => {
      const deleting = Boolean(replica.waitingForAgent || replica.deleteFailureCode || replica.deleteRequestedAt)
      return <article key={replica.id} className="space-y-4 rounded-xl border border-border bg-surface p-5 shadow-card" aria-label={tx("Replika {0}", { "0": index + 1 })}>
        <div className="flex flex-wrap items-center justify-between gap-3">
          <h3 className="font-medium text-text-heading">{tx("Replika")}{' '}{index + 1} · {replica.storageProvider}</h3>
          <StatusPill status={replica.status} />
        </div>
        {replica.waitingForAgent && <Notice tone="warning">{tx("Fiziksel silme için Agent bekleniyor. Agent yeniden bağlandığında silme işlemi devam edecek.")}</Notice>}
        {replica.status === 'Deleted' && <p className="text-sm text-text-muted">{tx("Fiziksel dosya kaldırıldı. Silinme:")}{' '}{formatDateTime(replica.deletedAt)}</p>}
        {replica.deleteFailureCode && !replica.waitingForAgent && <ErrorNotice title={tx("Silme tamamlanamadı")} {...describeFailure(replica.deleteFailureCode)} dataImpact={tx("Bu kopya depolamada duruyor; diğer kopyalar etkilenmedi.")} action={tx("Depolama bağlantısını kontrol edip silmeyi yeniden deneyin.")} />}
        <Facts columns={3}>
          {replica.storageTarget !== undefined && <Fact label={tx("Depolama hedefi")}>{replica.storageTarget}</Fact>}
          <Fact label={tx("Oluşturulma")}>{formatDateTime(replica.createdAt)}</Fact>
          <Fact label={tx("Boyut")}>{formatBytes(replica.sizeBytes)}</Fact>
          <Fact label={tx("Kullanılabilir")}>{replica.status === 'Deleted' ? tx('Hayır · geçmiş kaydı') : replica.usable ? tx('Evet · dosya korunuyor') : tx('Hayır')}</Fact>
          <Fact label={tx("Doğrulama durumu")}><StatusPill status={replica.verificationState} /></Fact>
          <Fact label={tx("Doğrulama yöntemi")}>{replica.verificationMethod}</Fact>
          <Fact label={tx("Doğrulama zamanı")}>{formatDateTime(replica.verifiedAt)}</Fact>
        </Facts>
        <details className="rounded-lg border border-border bg-surface-2 px-4 py-3" open={deleting}>
          <summary className="min-h-6 cursor-pointer text-sm font-medium text-text-heading">{tx("Checksum ve silme ayrıntıları")}</summary>
          <div className="pt-4"><Facts columns={3}>
            <Fact label={tx("Checksum")}><code className="break-all font-mono text-sm">{replica.checksum}</code></Fact>
            <Fact label={tx("Checksum algoritması")}>{replica.checksumAlgorithm}</Fact>
            <Fact label={tx("Silme yürütücüsü")}>{(replica.executionLocation ?? replica.deletionExecutor) === 'Agent' ? 'Agent' : 'Backend'}</Fact>
            <Fact label={tx("Silme istendi")}>{formatDateTime(replica.deleteRequestedAt)}</Fact>
            <Fact label={tx("Fiziksel silme en erken")}>{formatDateTime(replica.deleteAfter)}</Fact>
            {replica.deleteNextAttemptAt !== undefined && <Fact label={tx("Sonraki silme denemesi")}>{formatDateTime(replica.deleteNextAttemptAt)}</Fact>}
            {replica.deleteAttempts !== undefined && <Fact label={tx("Silme denemesi")}>{replica.deleteAttempts}</Fact>}
            {replica.deleteReason !== undefined && <Fact label={tx("Silme nedeni")}>{deletionReason(replica.deleteReason)}</Fact>}
          </Facts></div>
        </details>
      </article>
    })}
  </section>
}

/** Everything about one Backup, stacked: used where there is no room for tabs (Backup Run attempts, platform support). */
export function ArtifactDetails({ artifact }: { artifact: ArtifactPresentation }) {
  useLanguage()
  return <div className="space-y-6">
    <ArtifactDeletedNotice artifact={artifact} />
    <ArtifactSummary artifact={artifact} />
    <ArtifactIntegrity artifact={artifact} />
    <ArtifactEncryptionSection encryption={artifact.encryption} />
    <ArtifactRetention artifact={artifact} />
    <ArtifactMetadata artifact={artifact} />
    <ArtifactReplicas artifact={artifact} />
  </div>
}
