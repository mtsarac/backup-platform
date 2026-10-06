export interface BackupMetadata {
  engine: string | null
  databaseName: string | null
  backupType: string | null
  backupStartedAt: string | null
  backupFinishedAt: string | null
  instance: { serverName: string | null; instanceName: string | null; version: string | null; edition: string | null; hostPlatform: string | null } | null
  sqlDetails: { recoveryModel: string | null; compatibilityLevel: number | null; transparentDataEncryption: string | null; userAccess: string | null } | null
  verification: { state: string | null; method: string | null; startedAt: string | null; finishedAt: string | null } | null
  appliedOptions: { copyOnly: boolean | null; checksum: boolean | null; compression: boolean | null } | null
}
