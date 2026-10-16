import { describe, expect, it } from 'vitest'
import { ApiError } from '@/lib/api/client'
import { activeSecretFields, buildConfiguration, buildSecretValues, configurationComplete, initialFieldValues, secretsComplete } from './descriptor-values'
import { connectionErrorMessage, testMessage } from './labels'
import { connectionOptions } from './job-connection-picker-utils'
import { makeConnection, makeProvider } from './test-fixtures'
import type { ConfigField, SecretField } from './types'

describe('SFTP frontend contracts and behaviors', () => {
  const configFields: ConfigField[] = [
    { name: 'host', label: 'Host', kind: 'Text', required: true },
    { name: 'port', label: 'Port', kind: 'Number', required: true },
    { name: 'username', label: 'Kullanıcı adı', kind: 'Text', required: true },
    { name: 'authenticationType', label: 'Kimlik doğrulama yöntemi', kind: 'Text', required: true },
    { name: 'remoteBaseDirectory', label: 'Uzak dizin', kind: 'Text', required: true },
    { name: 'hostKeyFingerprint', label: 'Sunucu parmak izi', kind: 'Text', required: true },
  ]
  const secretFields: SecretField[] = [
    { name: 'password', label: 'Parola', required: true },
    { name: 'privateKey', label: 'Özel anahtar', required: true },
    { name: 'passphrase', label: 'Anahtar parolası', required: false },
  ]

  it('sets initial SFTP defaults to port 22 and password authentication', () => {
    const initial = initialFieldValues(configFields, {}, 'sftp')
    expect(initial.port).toBe('22')
    expect(initial.authenticationType).toBe('password')
    expect(initial.host).toBe('')
  })

  it('filters active secret inputs conditionally and marks passphrase optional', () => {
    const pwFields = activeSecretFields('sftp', { authenticationType: 'password' }, secretFields)
    expect(pwFields.map((field) => field.name)).toEqual(['password'])
    expect(pwFields[0].required).toBe(true)

    const keyFields = activeSecretFields('sftp', { authenticationType: 'private-key' }, secretFields)
    expect(keyFields.map((field) => field.name)).toEqual(['privateKey', 'passphrase'])
    expect(keyFields.find((field) => field.name === 'privateKey')?.required).toBe(true)
    expect(keyFields.find((field) => field.name === 'passphrase')?.required).toBe(false)
  })

  it('requires password for password auth and ignores non-selected secrets on submit', () => {
    const values = { host: 'sftp.internal', port: '22', username: 'backup-user', authenticationType: 'password', remoteBaseDirectory: '/backups', hostKeyFingerprint: 'SHA256:abc' }
    expect(configurationComplete(configFields, values)).toBe(true)
    const config = buildConfiguration(configFields, values)
    expect(config).toEqual({ host: 'sftp.internal', port: '22', username: 'backup-user', authenticationType: 'password', remoteBaseDirectory: '/backups', hostKeyFingerprint: 'SHA256:abc' })

    const pwFields = activeSecretFields('sftp', values, secretFields)
    expect(secretsComplete(pwFields, { password: '' })).toBe(false)
    expect(secretsComplete(pwFields, { password: 'secret-password', privateKey: 'should-omit' })).toBe(true)
    const payload = buildSecretValues(pwFields, { password: 'secret-password', privateKey: 'should-omit' })
    expect(payload).toEqual({ password: 'secret-password' })
  })

  it('requires privateKey and accepts omitted optional passphrase for private-key auth', () => {
    const keyFields = activeSecretFields('sftp', { authenticationType: 'private-key' }, secretFields)
    expect(secretsComplete(keyFields, {})).toBe(false)
    expect(secretsComplete(keyFields, { privateKey: '-----BEGIN OPENSSH PRIVATE KEY-----' })).toBe(true)
    const payload = buildSecretValues(keyFields, { privateKey: '-----BEGIN OPENSSH PRIVATE KEY-----', password: 'omit-password' })
    expect(payload).toEqual({ privateKey: '-----BEGIN OPENSSH PRIVATE KEY-----' })
  })

  it('labels backend failure codes truthfully without generic overrides', () => {
    expect(testMessage({ code: 'host_key_mismatch', message: 'Mismatch' })).toMatch(/parmak izi uyuşmuyor/)
    expect(testMessage({ code: 'authentication_failed', message: 'Auth failed' })).toMatch(/kimlik doğrulaması başarısız/)
    expect(testMessage({ code: 'timeout', message: 'Timed out' })).toMatch(/zaman aşımına/)
    expect(testMessage({ code: 'network_denied', message: 'Denied' })).toMatch(/güvenlik politikası/)
    expect(testMessage({ code: 'network_unavailable', message: 'Unavailable' })).toMatch(/ulaşılamıyor/)
  })

  it('words rotation and in-use errors cleanly', () => {
    expect(connectionErrorMessage(new ApiError(409, { error: 'rotation_test_failed' }))).toMatch(/Yeni kimlik bilgisi veya sunucu parmak izi/)
    expect(connectionErrorMessage(new ApiError(409, { error: 'connection_in_use' }))).toMatch(/kullanılıyor/)
  })

  it('formats connectionOptions for shared picker with and without tenant name', () => {
    const conn = makeConnection({ id: 'sftp-1', name: 'SFTP Primary', tenantName: 'Acme Corp', providerType: 'sftp', providerDisplayName: 'SFTP' })
    const provider = makeProvider({ providerType: 'sftp', displayName: 'SFTP', topology: 'BackendRelayed', executionLocation: 'Backend', supportedOwnerships: ['TenantManaged'] })
    const [tenantOption] = connectionOptions([conn], [provider], true)
    expect(tenantOption.label).toMatch(/^Acme Corp · SFTP Primary · SFTP · (?:Tenant|Çalışma alanı) yönetimli · Kullanılabilir$/)
    const [jobOption] = connectionOptions([conn], [provider], false)
    expect(jobOption.label).toMatch(/^SFTP Primary · SFTP · (?:Tenant|Çalışma alanı) yönetimli · Kullanılabilir$/)
  })
})
