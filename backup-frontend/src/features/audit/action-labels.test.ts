import { describe, expect, it } from 'vitest'
import { auditActionLabel } from './action-labels'

describe('audit action labels', () => {
  it.each([
    ['plan.created', 'Plan oluşturuldu'],
    ['support.restore.requested', 'Geri yükleme istendi'],
    ['break_glass.approved', 'Acil destek erişimi onaylandı'],
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
