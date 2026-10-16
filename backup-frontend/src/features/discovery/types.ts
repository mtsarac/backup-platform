export type DiscoveryEngine = 'mssql' | 'postgres'
export type DiscoverySource = 'Service' | 'Listener' | 'Docker'
export type DiscoverySupport = 'Supported' | 'Unsupported' | 'Unknown'

export type DiscoveryAgentStatus = {
  agentId: string; enabled: boolean; scanIntervalMinutes: number; includeDocker: boolean
  scanState: 'Idle' | 'Requested' | 'Running'; scanReason: string | null
  lastScanAt: string | null; lastSuccessfulScanAt: string | null
  lastOutcome: 'Completed' | 'Partial' | 'Failed' | null; lastFailureCode: string | null
  nextScanDueAt: string | null; instanceCount: number
  sources: { source: DiscoverySource; status: 'Scanned' | 'Unavailable' | 'Disabled' | 'Failed'; reasonCode: string | null }[]
}
export type DiscoveryInstanceSummary = {
  id: string; agentId: string; instanceKey: string; engine: DiscoveryEngine; source: DiscoverySource
  displayName: string; instanceName: string | null; host: string; port: number | null
  version: string | null; edition: string | null; hostPlatform: string | null
  containerName: string | null; containerImage: string | null
  presence: 'Present' | 'Missing'
  reachability: 'Reachable' | 'Unreachable' | 'Unknown'
  access: 'CredentialsRequired' | 'Authenticated' | 'AuthenticationFailed' | 'Unreachable' | 'Error' | 'Unknown'
  accessReasonCode: string | null
  fullBackup: DiscoverySupport; fullBackupReasonCode: string | null
  credential: { bound: boolean; credentialReference: string | null; integratedSecurity: boolean; encrypt: boolean; trustServerCertificate: boolean }
  firstSeenAt: string; lastSeenAt: string
  lastChange: { kind: 'Discovered' | 'Changed' | 'Recovered' | 'Unreachable' | 'Missing' | 'Reappeared'; at: string } | null
  databasesCheckedAt: string | null
  databaseCounts: { total: number; user: number; backupEligible: number }
}
export type DiscoveryDatabase = {
  name: string; isSystem: boolean; state: 'Online' | 'Offline' | 'Unknown'
  fullBackup: DiscoverySupport; fullBackupReasonCode: string | null
  presence: 'Present' | 'Missing'; lastSeenAt: string
  jobs: { id: string; name: string; enabled: boolean }[]
}
export type DiscoveryInstanceDetail = DiscoveryInstanceSummary & { databases: DiscoveryDatabase[] }

// Request bodies of the mutating endpoints.
export type DiscoveryCredentialInput = { credentialReference: string | null; databaseLogin?: { userName: string; password: string } | null; integratedSecurity: boolean; encrypt: boolean; trustServerCertificate: boolean }
export type DiscoverySettingsInput = { enabled: boolean; scanIntervalMinutes: number; includeDocker: boolean }
export type DiscoveryInstanceFilters = { agentId?: string; engine?: DiscoveryEngine; includeMissing?: boolean }
