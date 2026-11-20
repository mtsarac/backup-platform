import fs from 'node:fs'
import path from 'node:path'
import { describe, expect, it } from 'vitest'
import { auditActionLabel } from './action-labels'

describe('audit action labels', () => {
  it.each([
    ['plan.created', 'Plan oluşturuldu'],
    ['support.restore.requested', 'Geri yükleme istendi'],
    ['tenant_wrap_key.rotated', 'Şifreleme anahtarı yenilendi'],
    ['notification_transport.secret_cleared', 'Bildirim altyapısı gizli değeri temizlendi'],
  ])('one map answers %s with a plain sentence', (code, label) => {
    expect(auditActionLabel(code)).toBe(label)
  })

  it('has no label for a code it does not know, so callers decide how to say so', () => {
    expect(auditActionLabel('made.up_code')).toBeNull()
    expect(auditActionLabel('constructor')).toBeNull()
  })
})

/** Every action code the backend writes to the audit log: the constants of its audit-action classes and the literal codes passed where an audit event is recorded. */
function backendAuditActions(): string[] {
  const root = path.resolve(import.meta.dirname, '../../../../backup-backend/src')
  const files = fs.readdirSync(root, { recursive: true, encoding: 'utf8' }).filter(file => file.endsWith('.cs'))
  const codes = new Set<string>()
  for (const file of files) {
    const source = fs.readFileSync(path.join(root, file), 'utf8')
    for (const block of source.matchAll(/static class \w*AuditActions\b[\s\S]*?\n\}/g)) {
      for (const constant of block[0].matchAll(/const string \w+ = "([a-z_]+(?:\.[a-z_]+)+)"/g)) codes.add(constant[1])
    }
    for (const call of source.matchAll(/(?:AuditEvent\.Record|AddAudit|RecordSessionAudit)\(/g)) {
      for (const literal of source.slice(call.index, call.index + 500).matchAll(/"([a-z_]+(?:\.[a-z_]+)+)"/g)) codes.add(literal[1])
    }
  }
  return [...codes].sort()
}

describe('audit labels against the backend', () => {
  const codes = backendAuditActions()

  it('finds the backend audit actions', () => {
    expect(codes.length).toBeGreaterThan(100)
    expect(codes).toContain('backup_share.created')
  })

  it.each(codes)('labels %s', code => {
    expect(auditActionLabel(code), `no label for the audit action ${code}`).not.toBeNull()
  })
})
