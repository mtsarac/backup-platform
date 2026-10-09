import { describe, expect, it } from 'vitest'
import { scopeLabel, scopeLabels } from './labels'
import { parseQuotaScopeLabels, parseStorageConnectionIds } from './scope-input'

describe('quota scope labels', () => {
  it('accepts lowercase letters, digits and hyphens, deduplicated, split on commas and whitespace', () => {
    expect(parseQuotaScopeLabels('managed, tenant-2\n a1 ,managed')).toEqual({ ok: true, values: ['managed', 'tenant-2', 'a1'] })
    expect(parseQuotaScopeLabels('x'.repeat(64))).toEqual({ ok: true, values: ['x'.repeat(64)] })
  })

  it.each(['Managed', 'with space_underscore', 'tenant.scope', 'çay', 'x'.repeat(65)])('rejects %j', (value) => {
    expect(parseQuotaScopeLabels(value).ok).toBe(false)
  })

  it('requires at least one label', () => {
    expect(parseQuotaScopeLabels(' , ')).toEqual({ ok: false, error: expect.stringContaining('En az bir') })
  })
})

describe('storage connection GUIDs', () => {
  it('normalises to lowercase and deduplicates', () => {
    expect(parseStorageConnectionIds('3F2B8C1E-0D4A-4B6E-9A7C-5E1D2F3A4B5C, 3f2b8c1e-0d4a-4b6e-9a7c-5e1d2f3a4b5c')).toEqual({ ok: true, values: ['3f2b8c1e-0d4a-4b6e-9a7c-5e1d2f3a4b5c'] })
  })

  it.each(['not-a-guid', '3f2b8c1e0d4a4b6e9a7c5e1d2f3a4b5c', '{3f2b8c1e-0d4a-4b6e-9a7c-5e1d2f3a4b5c}', '3f2b8c1e-0d4a-4b6e-9a7c-5e1d2f3a4b5'])('rejects %j', (value) => {
    expect(parseStorageConnectionIds(value).ok).toBe(false)
  })

  it('requires at least one id', () => {
    expect(parseStorageConnectionIds('')).toEqual({ ok: false, error: expect.stringContaining('En az bir') })
  })
})

describe('scope labels', () => {
  it('words every selector kind in Turkish', () => {
    expect(scopeLabels([
      { kind: 'all' }, { kind: 'providerType', values: ['central', 'local'] }, { kind: 'ownershipType', values: ['PlatformManaged', 'TenantManaged'] },
      { kind: 'quotaScope', values: ['managed', 'tenant'] }, { kind: 'storageConnection', values: ['3f2b8c1e-0d4a-4b6e-9a7c-5e1d2f3a4b5c'] },
    ])).toEqual([
      'Tüm storage (All)', 'Provider türü: central, local', 'Sahiplik: Platform yönetimli, Tenant yönetimli', 'Kota kapsamı: managed, tenant', 'Depolama bağlantısı: 3f2b8c1e-0d4a-4b6e-9a7c-5e1d2f3a4b5c',
    ])
  })

  it('shows an unknown future kind verbatim instead of guessing', () => {
    expect(scopeLabel({ kind: 'region' as never })).toBe('region')
  })
})
