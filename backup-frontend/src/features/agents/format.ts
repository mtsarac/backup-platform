/** Known identifiers reported by the agent; anything else is shown by its raw identifier so nothing is hidden. */
export const capabilityLabels: Record<string, string> = {
  'mssql.backup.full': 'MSSQL Full Backup',
  'mssql.backup.checksum': 'CHECKSUM',
  'mssql.backup.compression': 'Compression',
  'mssql.backup.copy-only': 'COPY_ONLY',
  'mssql.restore.verify-only': 'VERIFYONLY',
  'mssql.backup.differential': 'Differential backup',
  'mssql.backup.log': 'Log backup',
  'mssql.restore.database': 'MSSQL Full Restore',
  'mssql.restore.precheck': 'MSSQL Restore Precheck',
  'mssql.restore.discovery': 'MSSQL Target Discovery',
  'postgres.backup.full': 'PostgreSQL Full Backup · pg_dump',
  'postgres.backup.checksum': 'PostgreSQL data_checksums',
  'postgres.backup.compression': 'PostgreSQL Archive Compression',
  'postgres.backup.copy-only': 'PostgreSQL Logical Copy',
  'postgres.restore.full': 'PostgreSQL Full Restore · pg_restore',
  'postgres.restore.precheck': 'PostgreSQL Restore Precheck',
  'postgres.restore.discovery': 'PostgreSQL Target Discovery',
  'postgres.restore.verify': 'PostgreSQL Archive Verification',
  'postgres.backup.differential': 'PostgreSQL Differential Backup',
  'postgres.backup.incremental': 'PostgreSQL Incremental Backup',
  'postgres.backup.wal': 'PostgreSQL WAL / PITR',
}

export const capabilitySupportLabels: Record<string, string> = {
  Supported: 'Destekleniyor',
  Unsupported: 'Desteklenmiyor',
  Unknown: 'Bilinmiyor',
}

export function agentDate(value: string | null | undefined) {
  return value ? new Date(value).toLocaleString('tr-TR') : 'Henüz bildirilmedi'
}
