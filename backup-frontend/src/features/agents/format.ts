import { tx } from '@/lib/i18n'
/** Known identifiers reported by the agent; anything else is shown by its raw identifier so nothing is hidden. */
export const capabilityLabels: Record<string, string> = {
  get 'mssql.backup.full'() { return tx("MSSQL Full Backup") },
  get 'mssql.backup.checksum'() { return tx("CHECKSUM") },
  get 'mssql.backup.compression'() { return tx("Compression") },
  get 'mssql.backup.copy-only'() { return tx("COPY_ONLY") },
  get 'mssql.restore.verify-only'() { return tx("VERIFYONLY") },
  get 'mssql.backup.differential'() { return tx("Differential backup") },
  get 'mssql.backup.log'() { return tx("Log backup") },
  get 'mssql.restore.database'() { return tx("MSSQL Full Restore") },
  get 'mssql.restore.precheck'() { return tx("MSSQL Restore Precheck") },
  get 'mssql.restore.discovery'() { return tx("MSSQL Target Discovery") },
  get 'postgres.backup.full'() { return tx("PostgreSQL Full Backup · pg_dump") },
  get 'postgres.backup.checksum'() { return tx("PostgreSQL data_checksums") },
  get 'postgres.backup.compression'() { return tx("PostgreSQL Archive Compression") },
  get 'postgres.backup.copy-only'() { return tx("PostgreSQL Logical Copy") },
  get 'postgres.restore.full'() { return tx("PostgreSQL Full Restore · pg_restore") },
  get 'postgres.restore.precheck'() { return tx("PostgreSQL Restore Precheck") },
  get 'postgres.restore.discovery'() { return tx("PostgreSQL Target Discovery") },
  get 'postgres.restore.verify'() { return tx("PostgreSQL Archive Verification") },
  get 'postgres.backup.differential'() { return tx("PostgreSQL Differential Backup") },
  get 'postgres.backup.incremental'() { return tx("PostgreSQL Incremental Backup") },
  'postgres.backup.wal': 'PostgreSQL WAL / PITR',
}

