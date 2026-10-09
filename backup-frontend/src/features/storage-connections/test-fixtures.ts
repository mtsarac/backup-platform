import type { ConnectionPermissions, ConnectionView, ProviderDescriptor } from './types'

export const noPermissions: ConnectionPermissions = { canEdit: false, canSetQuotaScope: false, canToggle: false, canDelete: false, canManageSecret: false, canTest: false }
export const tenantAdminPermissions: ConnectionPermissions = { canEdit: true, canSetQuotaScope: false, canToggle: true, canDelete: true, canManageSecret: true, canTest: true }

export function makeConnection(overrides: Partial<ConnectionView> = {}): ConnectionView {
  return {
    id: 'conn-local', tenantId: 'tenant-a', tenantName: 'Acme A.Ş.', name: 'Local', providerType: 'local', providerDisplayName: 'Local (Agent diski)', availability: 'Available',
    ownership: 'TenantManaged', quotaScope: 'tenant', enabled: true, administrativelyDisabled: false, effectiveEnabled: (overrides.enabled ?? true) && !(overrides.administrativelyDisabled ?? false), isDefault: true, capabilities: ['storage.read', 'storage.delete'], configuration: {},
    hasSecret: false, secretFieldNames: [], secretUpdatedAt: null, lastTest: null, replicaCount: 2, storedBytes: 6_200_000_000,
    createdAt: '2026-10-01T08:00:00Z', updatedAt: '2026-10-01T08:00:00Z', stateChangedAt: null, permissions: { ...noPermissions },
    ...overrides,
  }
}

export function makeManaged(overrides: Partial<ConnectionView> = {}): ConnectionView {
  return makeConnection({
    id: 'conn-central', name: 'Managed Storage', providerType: 'central', providerDisplayName: 'Managed Storage (Central)', ownership: 'PlatformManaged', quotaScope: 'managed',
    replicaCount: 3, storedBytes: 123_000_000, ...overrides,
  })
}

export function makeProvider(overrides: Partial<ProviderDescriptor> = {}): ProviderDescriptor {
  return {
    providerType: 'local', displayName: 'Local (Agent diski)', availability: 'Available', topology: 'AgentDirect', executionLocation: 'Agent', capabilities: ['storage.read', 'storage.delete'],
    supportedOwnerships: ['TenantManaged'], tenantCreatable: false, platformCreatable: false, maxConnectionsPerTenant: 1, configFields: [], secretFields: [], testMode: 'AgentSide',
    ...overrides,
  }
}

/** The M7a catalog: nothing is creatable by a tenant; central is creatable by the platform; sftp and google-drive are Planned. */
export const catalog: ProviderDescriptor[] = [
  makeProvider(),
  makeProvider({ providerType: 'central', displayName: 'Managed Storage (Central)', topology: 'BackendRelayed', executionLocation: 'Backend', supportedOwnerships: ['PlatformManaged'], platformCreatable: true, testMode: 'BackendRoundTrip' }),
  makeProvider({ providerType: 'sftp', displayName: 'SFTP', availability: 'Planned', topology: null, executionLocation: null, supportedOwnerships: ['TenantManaged'], tenantCreatable: true, maxConnectionsPerTenant: null, testMode: 'None', capabilities: ['storage.read'] }),
  makeProvider({ providerType: 'google-drive', displayName: 'Google Drive', availability: 'Planned', topology: null, executionLocation: null, supportedOwnerships: ['TenantManaged'], tenantCreatable: true, maxConnectionsPerTenant: null, testMode: 'None', capabilities: [] }),
]

/** A hypothetical creatable provider with config and secret fields, to prove the dialogs are generic. */
export const fakeProvider: ProviderDescriptor = makeProvider({
  providerType: 'fake-s3', displayName: 'Fake S3', supportedOwnerships: ['TenantManaged', 'PlatformManaged'], tenantCreatable: true, platformCreatable: true, maxConnectionsPerTenant: 2, testMode: 'BackendRoundTrip',
  configFields: [
    { name: 'bucket', label: 'Bucket', kind: 'Text', required: true, maxLength: 63 },
    { name: 'port', label: 'Port', kind: 'Number', required: false, maxLength: null },
    { name: 'useTls', label: 'TLS kullan', kind: 'Boolean', required: false, maxLength: null },
  ],
  secretFields: [{ name: 'accessKey', label: 'Access key', required: true }, { name: 'secretKey', label: 'Secret key', required: true }],
})
