import { describe, expect, it } from 'vitest'
import { screen } from '@testing-library/react'
import { AuditTable } from './audit-table'
import { renderPlatform } from './test-utils'
import type { PlatformAuditEntry } from './types'

const entry = (action: string, index: number): PlatformAuditEntry => ({
  id: `audit-${index}`, at: '2026-10-06T08:00:00Z', actorUserId: 'admin-id', actorDisplayName: 'Platform Operatörü', actorKind: 'PlatformAdmin',
  action, tenantId: null, tenantName: null, targetType: 'plan', targetId: 'plan-1', details: {},
})

describe('audit action labels', () => {
  it.each([
    ['plan.created', 'Plan oluşturuldu'], ['plan.changed', 'Plan değiştirildi'], ['plan.enabled', 'Plan etkinleştirildi'], ['plan.disabled', 'Plan devre dışı bırakıldı'],
    ['subscription.assigned', 'Abonelik atandı'], ['subscription.changed', 'Abonelik planı değiştirildi'], ['subscription.suspended', 'Abonelik askıya alındı'],
    ['subscription.resumed', 'Abonelik yeniden etkinleştirildi'], ['subscription.storage_limit_override_changed', 'Depolama sınırı için özel değer değiştirildi'],
    ['storage_connection.created', 'Depolama bağlantısı oluşturuldu'], ['storage_connection.updated', 'Depolama bağlantısı güncellendi'],
    ['storage_connection.enabled', 'Depolama bağlantısı etkinleştirildi'], ['storage_connection.disabled', 'Depolama bağlantısı devre dışı bırakıldı'],
    ['storage_connection.deleted', 'Depolama bağlantısı silindi'], ['storage_connection.tested', 'Depolama bağlantısı test edildi'],
    ['storage_connection.secret_rotated', 'Depolama bağlantısı kimlik bilgisi yenilendi'], ['storage_connection.secret_cleared', 'Depolama bağlantısı kimlik bilgisi temizlendi'],
    ['storage_connection.quota_scope_changed', 'Depolama bağlantısının kota kapsamı değiştirildi'],
  ])('renders %s as readable Turkish', (action, label) => {
    renderPlatform(<AuditTable rows={[entry(action, 1)]} emptyMessage="yok" />)
    expect(screen.getByText(label)).toBeInTheDocument()
    expect(screen.queryByText(action)).not.toBeInTheDocument()
  })
})
