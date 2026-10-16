import { describe, expect, it } from 'vitest'
import { ApiError } from '@/lib/api/client'
import { capabilityLabel, testMessage, configurationSummary, connectionErrorMessage, invalidFieldName, isPlatformCreatable, secretStatusLabel, tenantCreateState, testStatusMeta } from './labels'
import { catalog, fakeProvider, makeConnection } from './test-fixtures'

describe('tenantCreateState', () => {
  const [local, central, sftp, drive] = catalog
  it('refuses Planned, platform-only and over-limit providers and accepts the rest', () => {
    expect(tenantCreateState(sftp, [])).toEqual({ creatable: false, reason: 'Yakında / henüz kullanılamaz' })
    expect(tenantCreateState(drive, []).creatable).toBe(false)
    expect(tenantCreateState(local, []).creatable).toBe(false)
    expect(tenantCreateState(central, []).creatable).toBe(false)
    expect(tenantCreateState(fakeProvider, [])).toEqual({ creatable: true, reason: null })
    const two = [makeConnection({ id: '1', providerType: 'fake-s3' }), makeConnection({ id: '2', providerType: 'fake-s3' })]
    expect(tenantCreateState(fakeProvider, two)).toEqual({ creatable: false, reason: 'Bağlantı sınırına ulaşıldı (2)' })
    expect(tenantCreateState({ ...fakeProvider, maxConnectionsPerTenant: null }, two).creatable).toBe(true)
  })

  it('offers platform creation only for Available platform-creatable providers', () => {
    expect(catalog.filter(isPlatformCreatable).map((provider) => provider.providerType)).toEqual(['central'])
    expect(isPlatformCreatable({ ...catalog[2], platformCreatable: true })).toBe(false)
  })
})

describe('presentation helpers', () => {
  it('never maps NotTestable to a success tone', () => {
    expect(testStatusMeta.Succeeded.tone).toBe('success')
    expect(testStatusMeta.NotTestable.tone).toBe('neutral')
    expect(testStatusMeta.Failed.tone).toBe('error')
  })

  it('labels known capabilities and shows unknown ones verbatim', () => {
    expect(capabilityLabel('storage.read')).toBe('Okuma')
    expect(capabilityLabel('storage.atomic-publish')).toBe('Atomik yayımlama')
    expect(capabilityLabel('storage.enumerate')).toBe('Listeleme')
    expect(capabilityLabel('storage.verify-absence')).toBe("Silme doğrulaması")
    expect(capabilityLabel('storage.write')).toBe('storage.write')
    expect(capabilityLabel('storage.teleport')).toBe('storage.teleport')
  })

  it.each(['ok', 'agent_side_storage', 'storage_not_configured', 'provider_not_implemented', 'secret_unavailable', 'access_denied', 'invalid_configuration', 'insufficient_space', 'integrity_mismatch', 'not_supported', 'io_error', 'unexpected'])('maps test code %s to Turkish, not the English backend text', (code) => {
    const text = testMessage({ code, message: 'English backend sentence.' })
    expect(text).not.toBe('English backend sentence.')
    expect(text).toMatch(/[ıİşŞğĞüÜöÖçÇ]|\./)
  })

  it('falls back to the backend message only for an unknown or missing code', () => {
    expect(testMessage({ code: 'brand_new', message: 'Something new.' })).toBe('Something new.')
    expect(testMessage({ code: null, message: 'No code.' })).toBe('No code.')
  })

  it('states the secret presence and never a value', () => {
    expect(secretStatusLabel({ hasSecret: true })).toBe('Kimlik bilgisi kayıtlı')
    expect(secretStatusLabel({ hasSecret: false })).toBe('Yok')
  })

  it('summarises configuration with descriptor labels, falling back to the field name', () => {
    const connection = makeConnection({ configuration: { bucket: 'a', useTls: 'false', port: '', extra: 'x' } })
    expect(configurationSummary(connection, fakeProvider)).toEqual([
      { name: 'bucket', label: "Kova", value: 'a' }, { name: 'useTls', label: 'TLS kullan', value: 'Hayır' }, { name: 'port', label: "Bağlantı noktası", value: '—' }, { name: 'extra', label: 'extra', value: 'x' },
    ])
    expect(configurationSummary(makeConnection())).toEqual([])
  })

  it('words concurrent_update for connections, not plans', () => {
    expect(connectionErrorMessage(new ApiError(409, { error: 'concurrent_update' }))).toMatch(/^Bağlantı başka bir işlemle/)
    expect(connectionErrorMessage(new ApiError(409, { error: 'connection_in_use' }))).toMatch(/kullanılıyor/)
  })

  it('extracts the invalid configuration or secret field name', () => {
    const error = new ApiError(400, { error: 'invalid_request', field: 'configuration.bucket', code: 'required' })
    expect(invalidFieldName(error, 'configuration')).toBe('bucket')
    expect(invalidFieldName(error, 'secret')).toBeUndefined()
    expect(invalidFieldName(new Error('x'), 'secret')).toBeUndefined()
  })
})
