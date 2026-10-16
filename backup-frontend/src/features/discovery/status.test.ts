import { describe, expect, it } from 'vitest'
import { canCreateJob, databaseBlockedReason, databaseSelectable, isScanActive, supportsDiscovery } from './status'
import { makeDatabase, makeInstance } from './test-fixtures'

describe('canCreateJob', () => {
  it('needs a present, authenticated instance with host and port', () => {
    expect(canCreateJob(makeInstance())).toBe(true)
    expect(canCreateJob(makeInstance({ presence: 'Missing' }))).toBe(false)
    expect(canCreateJob(makeInstance({ access: 'CredentialsRequired' }))).toBe(false)
    expect(canCreateJob(makeInstance({ access: 'AuthenticationFailed' }))).toBe(false)
    expect(canCreateJob(makeInstance({ port: null }))).toBe(false)
    expect(canCreateJob(makeInstance({ host: '' }))).toBe(false)
  })
})
describe('databaseSelectable', () => {
  const instance = makeInstance()
  it('accepts an online user database that is not covered', () => expect(databaseSelectable(makeDatabase(), instance)).toBe(true))
  it.each([
    ['system', { isSystem: true }],
    ['offline', { state: 'Offline' as const }],
    ['unsupported full backup', { fullBackup: 'Unsupported' as const }],
    ['covered by a job', { jobs: [{ id: 'j', name: 'n', enabled: true }] }],
    ['missing', { presence: 'Missing' as const }],
  ])('rejects a %s database', (_name, patch) => {
    const database = makeDatabase(patch)
    expect(databaseSelectable(database, instance)).toBe(false)
    expect(databaseBlockedReason(database, instance)).toBeTruthy()
  })
  it('accepts Unknown full-backup support but never on an unusable instance', () => {
    expect(databaseSelectable(makeDatabase({ fullBackup: 'Unknown' }), instance)).toBe(true)
    expect(databaseSelectable(makeDatabase(), makeInstance({ access: 'CredentialsRequired' }))).toBe(false)
  })
})
describe('scan polling predicate', () => {
  it('polls only while a scan is requested or running', () => {
    expect(isScanActive(undefined)).toBe(false)
    expect(isScanActive([{ scanState: 'Idle' }])).toBe(false)
    expect(isScanActive([{ scanState: 'Idle' }, { scanState: 'Requested' }])).toBe(true)
    expect(isScanActive([{ scanState: 'Running' }])).toBe(true)
  })
})
describe('supportsDiscovery', () => {
  it('reads the discovery capability', () => {
    expect(supportsDiscovery([{ identifier: 'discovery.database-inventory', support: 'Supported' }])).toBe(true)
    expect(supportsDiscovery([{ identifier: 'discovery.database-inventory', support: 'Unsupported' }])).toBe(false)
    expect(supportsDiscovery([])).toBe(false)
  })
})
