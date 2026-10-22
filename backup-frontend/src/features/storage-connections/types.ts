/** Wire types of the storage-connections API (M7a). Every value is the backend's; `permissions` is computed for the caller and only read. */
export type ProviderAvailability = 'Available' | 'Planned'
export type ProviderTopology = 'AgentDirect' | 'BackendRelayed'
export type ExecutionLocation = 'Agent' | 'Backend'
export type ConnectionOwnership = 'PlatformManaged' | 'TenantManaged'
export type ConfigFieldKind = 'Text' | 'Number' | 'Boolean'
export type TestMode = 'BackendRoundTrip' | 'BackendReadAccess' | 'AgentSide' | 'None'
export type TestStatus = 'Succeeded' | 'Failed' | 'NotTestable'

export const connectionOwnerships: readonly ConnectionOwnership[] = ['PlatformManaged', 'TenantManaged']

export interface ConfigField {
  name: string
  label: string
  kind: ConfigFieldKind
  required: boolean
  maxLength?: number | null
}

export interface SecretField {
  name: string
  label: string
  required: boolean
}

export interface ProviderDescriptor {
  providerType: string
  displayName: string
  availability: ProviderAvailability
  topology: ProviderTopology | null
  executionLocation: ExecutionLocation | null
  capabilities: string[]
  supportedOwnerships: ConnectionOwnership[]
  tenantCreatable: boolean
  platformCreatable: boolean
  maxConnectionsPerTenant: number | null
  configFields: ConfigField[]
  secretFields: SecretField[]
  testMode: TestMode
}

export type ConfigurationValue = string
export type ConnectionConfiguration = Record<string, ConfigurationValue>

export interface ConnectionTest {
  status: TestStatus
  code: string | null
  message: string
  testedAt: string
}

export interface ConnectionPermissions {
  canEdit: boolean
  canSetQuotaScope: boolean
  canToggle: boolean
  canDelete: boolean
  canManageSecret: boolean
  canTest: boolean
}

export interface ConnectionView {
  id: string
  tenantId: string
  /** Platform lists only. */
  tenantName: string | null
  name: string
  providerType: string
  providerDisplayName: string
  availability: ProviderAvailability
  ownership: ConnectionOwnership
  quotaScope: string
  enabled: boolean
  administrativelyDisabled: boolean
  effectiveEnabled: boolean
  isDefault: boolean
  capabilities: string[]
  /** Safe, secret-free values keyed by the descriptor's config fields. */
  configuration: ConnectionConfiguration
  hasSecret: boolean
  secretFieldNames: string[]
  secretUpdatedAt: string | null
  lastTest: ConnectionTest | null
  /** Replicas still occupying storage (Available / DeletePending / Deleting / DeleteFailed). */
  replicaCount: number
  storedBytes: number
  createdAt: string
  updatedAt: string
  stateChangedAt: string | null
  permissions: ConnectionPermissions
}

export interface ConnectionTestResponse {
  connection: ConnectionView
  test: ConnectionTest & { durationMs: number }
}

/** Secret values keyed by the descriptor's secret field names. Only ever sent, never stored, cached or rendered. */
export type SecretValues = Record<string, string>

export interface TenantCreateConnectionInput {
  providerType: string
  name: string
  configuration?: ConnectionConfiguration
  secret?: SecretValues
}

export interface TenantUpdateConnectionInput {
  name?: string
  configuration?: ConnectionConfiguration
}

export interface PlatformCreateConnectionInput extends TenantCreateConnectionInput {
  ownership?: ConnectionOwnership
  quotaScope?: string
}

export interface PlatformUpdateConnectionInput extends TenantUpdateConnectionInput {
  quotaScope?: string
}

export interface PlatformConnectionFilters {
  ownership?: ConnectionOwnership
  providerType?: string
  tenantId?: string
  enabled?: boolean
  search?: string
  page?: number
  pageSize?: number
}

export type StorageCapacityStatus = 'Available' | 'Warning' | 'Critical' | 'CapacityUnavailable' | 'Pending'

export interface StorageCapacitySnapshot {
  connectionId: string
  taskId?: string | null
  historyLimit?: number
  history?: Array<{
    reportedAt: string
    totalBytes: number | null
    usedBytes: number | null
    freeBytes: number | null
    status: Exclude<StorageCapacityStatus, 'Pending'>
  }>
  totalBytes: number | null
  usedBytes: number | null
  freeBytes: number | null
  status: StorageCapacityStatus
  reportedAt: string
  errorMessage?: string | null
}
