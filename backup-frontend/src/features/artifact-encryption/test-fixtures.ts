import type { BreakGlassRequest, TenantEncryption, WrapKey } from './types'

export const keyId = 'a'.repeat(32)

export function makeWrapKey(overrides: Partial<WrapKey> = {}): WrapKey {
  return { version: 1, keyId, state: 'Active', createdAt: '2026-10-01T10:00:00Z', artifactCount: 3, ...overrides }
}

export function makeBreakGlass(overrides: Partial<BreakGlassRequest> = {}): BreakGlassRequest {
  return {
    id: 'request-a', tenantId: 'tenant-a', tenantName: 'Acme', artifactId: '11111111-1111-1111-1111-111111111111', status: 'Pending',
    reason: 'Restore failed, incident 4711', createdAt: '2026-10-19T10:00:00Z', expiresAt: '2026-10-19T11:00:00Z', serverNow: '2026-10-19T10:05:00Z',
    requestedBy: 'admin-1', requestedByName: 'Ada Admin', decidedBy: null, decidedByName: null, decidedAt: null, decisionReason: null,
    consumedAt: null, downloadId: null, isRequester: false, canApprove: true, canDownload: false, ...overrides,
  }
}

export function makeTenantEncryption(overrides: Partial<TenantEncryption> = {}): TenantEncryption {
  return {
    platformRecovery: { configured: true, keyId: 'b'.repeat(32), keyVersion: 1 },
    tenantRecovery: null,
    wrapKey: { version: 2, keyId, state: 'Active' },
    ...overrides,
  }
}
