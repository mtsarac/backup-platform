import { describe, it, expect, vi } from 'vitest'
import { screen, fireEvent } from '@testing-library/react'
import { render } from '@/test/render'
import { MultiDestinationPicker } from './multi-destination-picker'
import type { BackupConfiguration } from '@/features/backup-jobs/types'
import type { ConnectionView, ProviderDescriptor } from './types'

const dummyProviders: ProviderDescriptor[] = [
  {
    providerType: 'local',
    displayName: 'Local (Agent diski)',
    availability: 'Available',
    topology: 'AgentDirect',
    executionLocation: 'Agent',
    capabilities: [],
    supportedOwnerships: ['TenantManaged'],
    tenantCreatable: true,
    platformCreatable: false,
    maxConnectionsPerTenant: 1,
    configFields: [],
    secretFields: [],
    testMode: 'None'
  },
  {
    providerType: 'central',
    displayName: 'Managed Storage (Central)',
    availability: 'Available',
    topology: 'BackendRelayed',
    executionLocation: 'Backend',
    capabilities: [],
    supportedOwnerships: ['PlatformManaged'],
    tenantCreatable: false,
    platformCreatable: true,
    maxConnectionsPerTenant: 1,
    configFields: [],
    secretFields: [],
    testMode: 'None'
  },
  {
    providerType: 'sftp',
    displayName: 'SFTP',
    availability: 'Available',
    topology: 'BackendRelayed',
    executionLocation: 'Backend',
    capabilities: [],
    supportedOwnerships: ['TenantManaged'],
    tenantCreatable: true,
    platformCreatable: false,
    maxConnectionsPerTenant: null,
    configFields: [],
    secretFields: [],
    testMode: 'BackendRoundTrip'
  }
]

const dummyConnections: ConnectionView[] = [
  {
    id: 'conn-local',
    tenantId: 'tenant-1',
    tenantName: null,
    name: 'Default Local',
    providerType: 'local',
    providerDisplayName: 'Local (Agent diski)',
    availability: 'Available',
    ownership: 'TenantManaged',
    quotaScope: 'Default',
    enabled: true,
    administrativelyDisabled: false,
    effectiveEnabled: true,
    isDefault: true,
    capabilities: [],
    configuration: {},
    hasSecret: false,
    secretFieldNames: [],
    secretUpdatedAt: null,
    lastTest: null,
    replicaCount: 0,
    storedBytes: 0,
    createdAt: '',
    updatedAt: '',
    stateChangedAt: null,
    permissions: { canEdit: true, canSetQuotaScope: false, canToggle: true, canDelete: false, canManageSecret: false, canTest: false }
  },
  {
    id: 'conn-sftp-1',
    tenantId: 'tenant-1',
    tenantName: null,
    name: 'Secondary SFTP',
    providerType: 'sftp',
    providerDisplayName: 'SFTP',
    availability: 'Available',
    ownership: 'TenantManaged',
    quotaScope: 'Default',
    enabled: true,
    administrativelyDisabled: false,
    effectiveEnabled: true,
    isDefault: false,
    capabilities: [],
    configuration: {},
    hasSecret: true,
    secretFieldNames: [],
    secretUpdatedAt: null,
    lastTest: null,
    replicaCount: 0,
    storedBytes: 0,
    createdAt: '',
    updatedAt: '',
    stateChangedAt: null,
    permissions: { canEdit: true, canSetQuotaScope: false, canToggle: true, canDelete: true, canManageSecret: true, canTest: true }
  }
]

describe('MultiDestinationPicker', () => {
  it('renders a canonical API response whose primary is not the first destination', () => {
    const configuration = {
      engine: 'mssql', database: 'TestDB', host: 'localhost', port: null,
      integratedSecurity: true, databaseCredentialReference: null,
      encrypt: true, trustServerCertificate: false, stagingReference: 'default',
      storagePrefix: '', copyOnly: true, checksumPolicy: 'Required', compressionPolicy: 'Preferred',
      destinations: [
        { storageProvider: 'local', storageConnectionId: 'conn-local', storageExecutionLocation: 'Agent', isPrimary: false },
        { storageProvider: 'sftp', storageConnectionId: 'conn-sftp-1', storageExecutionLocation: 'Backend', isPrimary: true },
      ],
      requiredVerifiedReplicaCount: 1,
    } as BackupConfiguration
    const onChange = vi.fn()
    render(<MultiDestinationPicker configuration={configuration} connections={dummyConnections} providers={dummyProviders} pending={false} error={null} existingTarget={true} onChange={onChange} onRefresh={() => {}} />)
    expect(screen.getByLabelText("Depolama Sağlayıcısı")).toHaveValue('sftp')
    expect(screen.getByLabelText('Depolama bağlantısı')).toHaveValue('conn-sftp-1')
    fireEvent.change(screen.getByLabelText('Depolama bağlantısı'), { target: { value: 'conn-local' } })
    expect(onChange.mock.calls[0][0].destinations[0]).toEqual(configuration.destinations![0])
    expect(onChange.mock.calls[0][0].destinations[1]).toMatchObject({ storageConnectionId: 'conn-local', isPrimary: true })
    expect(onChange.mock.calls[0][0]).not.toHaveProperty('storageProvider')
  })

  it('renders primary destination and allows adding a second destination', () => {
    const config: BackupConfiguration = {
      engine: 'mssql', database: 'TestDB', host: 'localhost', port: null,
      integratedSecurity: true, databaseCredentialReference: null,
      encrypt: true, trustServerCertificate: false, stagingReference: 'default',
      storagePrefix: '', copyOnly: true,
      checksumPolicy: 'Required', compressionPolicy: 'Preferred', encryptionPolicy: 'Off',
      destinations: [
        { storageProvider: 'local', storageConnectionId: 'conn-local', storageExecutionLocation: 'Agent', isPrimary: true }
      ],
      requiredVerifiedReplicaCount: 1
    }

    const onChange = vi.fn()
    render(
      <MultiDestinationPicker
        configuration={config}
        connections={dummyConnections}
        providers={dummyProviders}
        pending={false}
        error={null}
        existingTarget={true}
        onChange={onChange}
        onRefresh={() => {}}
      />
    )

    expect(screen.getByText("Birincil")).toBeInTheDocument()
    expect(screen.getByText('Toplam 1 hedef')).toBeInTheDocument()

    // Add secondary SFTP
    const select = screen.getByLabelText("Yeni Çoğaltma Hedefi Ekle")
    fireEvent.change(select, { target: { value: 'conn-sftp-1' } })

    expect(onChange).toHaveBeenCalled()
    const updated = onChange.mock.calls[0][0] as BackupConfiguration
    expect(updated.destinations).toHaveLength(2)
    expect(updated.destinations[1]).toMatchObject({
      storageProvider: 'sftp',
      storageConnectionId: 'conn-sftp-1',
      storageExecutionLocation: 'Backend',
      isPrimary: false
    })
  })

  it('allows changing primary destination and removing a destination', () => {
    const config: BackupConfiguration = {
      engine: 'mssql', database: 'TestDB', host: 'localhost', port: null,
      integratedSecurity: true, databaseCredentialReference: null,
      encrypt: true, trustServerCertificate: false, stagingReference: 'default',
      storagePrefix: '', copyOnly: true,
      checksumPolicy: 'Required', compressionPolicy: 'Preferred', encryptionPolicy: 'Off',
      destinations: [
        { storageProvider: 'local', storageConnectionId: 'conn-local', storageExecutionLocation: 'Agent', isPrimary: true },
        { storageProvider: 'sftp', storageConnectionId: 'conn-sftp-1', storageExecutionLocation: 'Backend', isPrimary: false }
      ],
      requiredVerifiedReplicaCount: 2
    }

    const onChange = vi.fn()
    render(
      <MultiDestinationPicker
        configuration={config}
        connections={dummyConnections}
        providers={dummyProviders}
        pending={false}
        error={null}
        existingTarget={true}
        onChange={onChange}
        onRefresh={() => {}}
      />
    )

    expect(screen.getByText('Toplam 2 hedef')).toBeInTheDocument()

    // Change primary to SFTP
    const makePrimaryBtn = screen.getByRole('button', { name: "Birincil Yap" })
    fireEvent.click(makePrimaryBtn)

    expect(onChange).toHaveBeenCalled()
    const updatedPrimary = onChange.mock.calls[0][0] as BackupConfiguration
    expect(updatedPrimary.destinations.find(d => d.isPrimary)).toMatchObject({ storageProvider: 'sftp', storageConnectionId: 'conn-sftp-1' })
    expect(updatedPrimary.destinations[1].isPrimary).toBe(true)
    expect(updatedPrimary.destinations[0].isPrimary).toBe(false)

    // Remove SFTP destination
    const removeBtn = screen.getByRole('button', { name: /SFTP hedefini kaldır/i })
    fireEvent.click(removeBtn)

    const updatedRemoved = onChange.mock.calls[1][0] as BackupConfiguration
    expect(updatedRemoved.destinations).toHaveLength(1)
    expect(updatedRemoved.requiredVerifiedReplicaCount).toBe(1)
  })

  it('edits the primary destination in place and leaves replicas untouched', () => {
    const config: BackupConfiguration = {
      engine: 'mssql', database: 'TestDB', host: 'localhost', port: null,
      integratedSecurity: true, databaseCredentialReference: null,
      encrypt: true, trustServerCertificate: false, stagingReference: 'default',
      storagePrefix: '', copyOnly: true,
      checksumPolicy: 'Required', compressionPolicy: 'Preferred', encryptionPolicy: 'Off',
      destinations: [
        { storageProvider: 'local', storageConnectionId: 'conn-local', storageExecutionLocation: 'Agent', isPrimary: false },
        { storageProvider: 'sftp', storageConnectionId: 'conn-sftp-1', storageExecutionLocation: 'Backend', isPrimary: true }
      ],
      requiredVerifiedReplicaCount: 2
    }
    const onChange = vi.fn()
    render(<MultiDestinationPicker configuration={config} connections={dummyConnections} providers={dummyProviders} pending={false} error={null} existingTarget={true} onChange={onChange} onRefresh={() => {}} />)

    expect(screen.getByLabelText('Depolama bağlantısı')).toHaveValue('conn-sftp-1')
    fireEvent.change(screen.getByLabelText("Depolama Sağlayıcısı"), { target: { value: 'central' } })

    const updated = onChange.mock.calls[0][0] as BackupConfiguration
    expect(updated.destinations).toEqual([
      config.destinations[0],
      { storageProvider: 'central', storageConnectionId: null, storageExecutionLocation: 'Backend', isPrimary: true }
    ])
  })

  it('shows execution guard warning when >1 destination is configured', () => {
    const config: BackupConfiguration = {
      engine: 'mssql', database: 'TestDB', host: 'localhost', port: null,
      integratedSecurity: true, databaseCredentialReference: null,
      encrypt: true, trustServerCertificate: false, stagingReference: 'default',
      storagePrefix: '', copyOnly: true,
      checksumPolicy: 'Required', compressionPolicy: 'Preferred', encryptionPolicy: 'Off',
      destinations: [
        { storageProvider: 'local', storageConnectionId: 'conn-local', storageExecutionLocation: 'Agent', isPrimary: true },
        { storageProvider: 'sftp', storageConnectionId: 'conn-sftp-1', storageExecutionLocation: 'Backend', isPrimary: false }
      ],
      requiredVerifiedReplicaCount: 2
    }

    render(
      <MultiDestinationPicker
        configuration={config}
        connections={dummyConnections}
        providers={dummyProviders}
        pending={false}
        error={null}
        existingTarget={true}
        onChange={() => {}}
        onRefresh={() => {}}
      />
    )

    expect(screen.getByText(/(?:Each destination is written and verified independently|Her hedefe ayrı yazılır ve bağımsız olarak doğrulanır)/i)).toBeInTheDocument()
  })
})
