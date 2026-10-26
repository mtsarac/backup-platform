import { describe, expect, it } from 'vitest'
import { databaseStateLabel, precheckLabel, precheckWarningText } from './presentation'

describe('restore presentation', () => {
  it('names a reported database state and keeps unknown ones unknown', () => {
    expect(databaseStateLabel('ONLINE')).toBe('Çevrimiçi')
    expect(databaseStateLabel('online')).toBe('Çevrimiçi')
    expect(databaseStateLabel('RECOVERY_PENDING')).toBe('Kurtarma Bekleniyor')
    expect(databaseStateLabel('SOMETHING_ELSE')).toBe('Bilinmiyor')
    expect(databaseStateLabel(null)).toBe('Bilinmiyor')
  })

  it('maps the check ids the PostgreSQL provider and the agent report', () => {
    for (const id of ['postgres.tools', 'postgres.restore', 'postgres.restore-plan', 'postgres.restore-permission', 'postgres.target', 'postgres.archive-compatibility', 'postgres.verification-scope', 'postgres.archive-trust', 'postgres.roles', 'source.key', 'source.storage', 'source.replica', 'file-system.source']) {
      expect(precheckLabel(id), id).not.toBe('Diğer kontrol')
    }
    expect(precheckLabel('unknown.check')).toBe('Diğer kontrol')
  })

  it('shows engine warnings in Turkish by reason code and falls back to the engine message', () => {
    const roles = precheckWarningText({ reasonCode: 'role-definitions-not-in-archive', message: 'Cluster role definitions are not included.' })
    expect(roles).toContain('Rol tanımları yedeğe dahil değildir')
    expect(precheckWarningText({ reasonCode: 'brand-new-code', message: 'Engine text' })).toBe('Engine text')
  })
})
