import type { TenantEncryption, WrapKey } from './types'

export const keyId = 'a'.repeat(32)

export function makeWrapKey(overrides: Partial<WrapKey> = {}): WrapKey {
  return { version: 1, keyId, state: 'Active', createdAt: '2026-10-01T10:00:00Z', artifactCount: 3, ...overrides }
}

export function makeTenantEncryption(overrides: Partial<TenantEncryption> = {}): TenantEncryption {
  return {
    platformRecovery: { configured: true, keyId: 'b'.repeat(32), keyVersion: 1 },
    tenantRecovery: null,
    wrapKey: { version: 2, keyId, state: 'Active' },
    ...overrides,
  }
}
