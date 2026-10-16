import type { BackupConfiguration } from '@/features/backup-jobs/types'
import { databaseSelectable } from './status'
import type { DiscoveryInstanceDetail } from './types'

/** Engine defaults mirror the wizard's manual engine switch so a discovered source behaves like a hand-entered one. */
const engineDefaults = {
  mssql: { copyOnly: true, checksumPolicy: 'Required' },
  postgres: { copyOnly: false, checksumPolicy: 'Preferred' },
} as const

/** Source fields of the wizard configuration for a discovered instance; `database` is the first pick (one job is created per database). */
export function discoveredConfiguration(base: BackupConfiguration, instance: DiscoveryInstanceDetail, databases: readonly string[]): BackupConfiguration {
  const { credential } = instance
  const integratedSecurity = instance.engine === 'mssql' && credential.integratedSecurity
  return {
    ...base, ...engineDefaults[instance.engine], engine: instance.engine, host: instance.host, port: instance.port,
    database: databases[0] ?? '', integratedSecurity, databaseCredentialReference: integratedSecurity ? null : credential.credentialReference,
    encrypt: credential.encrypt, trustServerCertificate: credential.trustServerCertificate,
  }
}

/** Keeps only names that are still selectable, so a stale link can never preselect a covered or system database. */
export const selectableNames = (instance: DiscoveryInstanceDetail, names: readonly string[]) =>
  names.filter(name => instance.databases.some(db => db.name === name && databaseSelectable(db, instance)))
