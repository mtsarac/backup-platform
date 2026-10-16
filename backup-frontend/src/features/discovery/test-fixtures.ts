import type { DiscoveryAgentStatus, DiscoveryDatabase, DiscoveryInstanceDetail, DiscoveryInstanceSummary } from './types'

export const makeInstance = (patch: Partial<DiscoveryInstanceSummary> = {}): DiscoveryInstanceSummary => ({
  id: 'inst-1', agentId: 'agent-a', instanceKey: 'mssql|service|default', engine: 'mssql', source: 'Service', displayName: 'SQL Server (default)', instanceName: null,
  host: 'localhost', port: 1433, version: '16.0', edition: 'Developer', hostPlatform: 'Windows', containerName: null, containerImage: null,
  presence: 'Present', reachability: 'Reachable', access: 'Authenticated', accessReasonCode: null, fullBackup: 'Supported', fullBackupReasonCode: null,
  credential: { bound: true, credentialReference: 'sql-main', integratedSecurity: false, encrypt: true, trustServerCertificate: true },
  firstSeenAt: '2026-10-01T08:00:00Z', lastSeenAt: '2026-10-02T08:00:00Z', lastChange: { kind: 'Discovered', at: '2026-10-01T08:00:00Z' }, databasesCheckedAt: '2026-10-02T08:00:00Z',
  databaseCounts: { total: 3, user: 2, backupEligible: 2 }, ...patch,
})
export const makeDatabase = (patch: Partial<DiscoveryDatabase> = {}): DiscoveryDatabase => ({
  name: 'ERP', isSystem: false, state: 'Online', fullBackup: 'Supported', fullBackupReasonCode: null, presence: 'Present', lastSeenAt: '2026-10-02T08:00:00Z', jobs: [], ...patch,
})
export const makeDetail = (patch: Partial<DiscoveryInstanceDetail> = {}): DiscoveryInstanceDetail => ({
  ...makeInstance(), databases: [makeDatabase({ name: 'ERP' }), makeDatabase({ name: 'CRM' }), makeDatabase({ name: 'master', isSystem: true }), makeDatabase({ name: 'Covered', jobs: [{ id: 'job-9', name: 'Covered job', enabled: true }] })], ...patch,
})
export const makeStatus = (patch: Partial<DiscoveryAgentStatus> = {}): DiscoveryAgentStatus => ({
  agentId: 'agent-a', enabled: true, scanIntervalMinutes: 360, includeDocker: true, scanState: 'Idle', scanReason: null, lastScanAt: '2026-10-02T08:00:00Z', lastSuccessfulScanAt: '2026-10-02T08:00:00Z',
  lastOutcome: 'Completed', lastFailureCode: null, nextScanDueAt: null, instanceCount: 1, sources: [{ source: 'Service', status: 'Scanned', reasonCode: null }, { source: 'Docker', status: 'Unavailable', reasonCode: 'docker_not_found' }], ...patch,
})
export const supportedAgent = { agentId: 'agent-a', hostName: 'SQL Agent', operationalStatus: 'Online', capabilities: [{ identifier: 'discovery.database-inventory', support: 'Supported' }] }
export const legacyAgent = { agentId: 'agent-old', hostName: 'Old Agent', operationalStatus: 'Online', capabilities: [] }
