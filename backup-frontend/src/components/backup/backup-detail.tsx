import { Badge, StatusBadge } from '@/components/ui/badge'
import { Link } from 'react-router-dom'
import { ROUTES } from '@/lib/routes'
import type { Backup, BackupType } from '@/lib/types'

function assertNever(value: never): never {
  throw new Error(`Unexpected value: ${String(value)}`)
}

function backupTypeLabel(type: BackupType): string {
  return type === 'mssql-full' ? 'MSSQL Full' : type
}

function verificationLabel(status: Backup['sqlVerificationStatus']): string {
  switch (status) {
    case 'verified':
      return 'Doğrulandı'
    case 'not-verified':
      return 'Doğrulanmadı'
    case 'failed':
      return 'Başarısız'
    default:
      return assertNever(status)
  }
}

function verificationTone(status: Backup['sqlVerificationStatus']) {
  switch (status) {
    case 'verified':
      return 'success'
    case 'not-verified':
      return 'warning'
    case 'failed':
      return 'error'
    default:
      return assertNever(status)
  }
}

function formatBytes(bytes?: number): string {
  if (!bytes) return '-'
  return `${(bytes / 1024 ** 3).toFixed(1)} GB`
}

function formatDuration(seconds?: number): string {
  if (!seconds) return 'Devam ediyor'
  const minutes = Math.floor(seconds / 60)
  const remainder = seconds % 60
  return `${minutes} dk ${remainder} sn`
}

function boolLabel(value?: boolean): string {
  return value ? 'Açık' : 'Kapalı'
}

function DetailRow({ label, value }: { label: string; value: React.ReactNode }) {
  return (
    <div className="flex justify-between gap-4 border-b border-border py-2 last:border-0">
      <dt className="whitespace-nowrap text-text-muted">{label}</dt>
      <dd className="min-w-0 break-words text-right text-text">{value}</dd>
    </div>
  )
}

export function BackupDetail({ backup }: { backup: Backup }) {
  return (
    <div className="rounded-lg border border-border bg-surface p-4">
      <div className="mb-4 flex flex-wrap items-center justify-between gap-3">
        <div>
          <h2 className="text-lg font-medium text-text-heading">Backup Detayı</h2>
          <p className="text-sm text-text-muted">{backup.storagePath}</p>
        </div>
        <div className="flex items-center gap-2">
          <StatusBadge status={backup.status} />
          <Link
            to={ROUTES.RESTORE.replace(':id', backup.id)}
            className="rounded-md border border-border px-3 py-1.5 text-sm font-medium text-primary hover:bg-surface-2"
          >
            Restore
          </Link>
        </div>
      </div>

      <dl className="text-sm">
        <DetailRow label="SQL Server Instance" value={backup.instanceId ?? '-'} />
        <DetailRow label="SQL Server Version" value={backup.sqlVersion ?? '-'} />
        <DetailRow label="SQL Server Edition" value={backup.sqlEdition ?? '-'} />
        <DetailRow label="Database" value={backup.databaseName ?? '-'} />
        <DetailRow label="Compatibility Level" value={backup.compatibilityLevel ?? '-'} />
        <DetailRow label="Recovery Model" value={backup.recoveryModel ?? '-'} />
        <DetailRow label="Backup Tipi" value={backupTypeLabel(backup.type)} />
        <DetailRow label="Compression" value={boolLabel(backup.compression)} />
        <DetailRow label="Encryption" value={boolLabel(backup.encryption)} />
        <DetailRow label="Checksum" value={boolLabel(backup.checksum)} />
        <DetailRow
          label="SQL Doğrulama"
          value={<Badge tone={verificationTone(backup.sqlVerificationStatus)}>{verificationLabel(backup.sqlVerificationStatus)}</Badge>}
        />
        <DetailRow
          label="Storage Doğrulama"
          value={<Badge tone={verificationTone(backup.storageVerificationStatus)}>{verificationLabel(backup.storageVerificationStatus)}</Badge>}
        />
        <DetailRow label="Dosya Boyutu" value={formatBytes(backup.fileSizeBytes)} />
        <DetailRow label="Süre" value={formatDuration(backup.durationSeconds)} />
        <DetailRow label="Başlangıç" value={new Date(backup.startedAt).toLocaleString('tr-TR')} />
        <DetailRow label="Bitiş" value={backup.completedAt ? new Date(backup.completedAt).toLocaleString('tr-TR') : '-'} />
      </dl>
    </div>
  )
}
