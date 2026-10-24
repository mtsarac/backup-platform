import { describe, expect, it } from 'vitest'
import { canCreateJob, databaseAvailability, databaseSelectable, isScanActive, supportsDiscovery } from './status'
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
    expect(databaseAvailability(database, instance).reason).toBeTruthy()
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
describe('databaseAvailability', () => {
  const instance = makeInstance()
  it('calls an online user database that no job covers backupable, without a reason', () => {
    expect(databaseAvailability(makeDatabase(), instance)).toEqual({ kind: 'available', reason: null })
  })
  it('tells an already covered database apart from an unbackupable one', () => {
    const covered = databaseAvailability(makeDatabase({ jobs: [{ id: 'j', name: 'n', enabled: true }] }), instance)
    expect(covered.kind).toBe('covered')
    expect(covered.reason).toBeTruthy()
  })
  it.each([
    ['a system database', { isSystem: true }],
    ['an offline database', { state: 'Offline' as const }],
    ['a database without full backup support', { fullBackup: 'Unsupported' as const, fullBackupReasonCode: 'connections-disallowed' }],
    ['a database no longer seen', { presence: 'Missing' as const }],
  ])('gives %s a plain reason', (_name, patch) => {
    const result = databaseAvailability(makeDatabase(patch), instance)
    expect(result.kind).toBe('unavailable')
    expect(result.reason).toBeTruthy()
  })
  it('never shows a raw reason code as the reason', () => {
    const result = databaseAvailability(makeDatabase({ fullBackup: 'Unsupported', fullBackupReasonCode: 'weird_new_code' }), instance)
    expect(result.kind).toBe('unavailable')
    expect(result.reason).not.toContain('weird_new_code')
  })
  it('is unavailable for every database of an unusable instance', () => {
    expect(databaseAvailability(makeDatabase(), makeInstance({ access: 'CredentialsRequired' })).kind).toBe('unavailable')
  })
})
