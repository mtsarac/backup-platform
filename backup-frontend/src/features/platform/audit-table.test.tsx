import { describe, expect, it } from 'vitest'
import { screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { AuditTable } from './audit-table'
import { renderPlatform } from './test-utils'
import type { PlatformAuditEntry } from './types'

const entry = (action: string, index: number, overrides: Partial<PlatformAuditEntry> = {}): PlatformAuditEntry => ({
  id: `audit-${index}`, at: '2026-10-06T08:00:00Z', actorUserId: 'admin-id', actorDisplayName: 'Platform Operatörü', actorKind: 'PlatformAdmin',
  action, tenantId: null, tenantName: null, targetType: 'plan', targetId: 'plan-1', details: {}, ...overrides,
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
    ['support.session.opened', 'Destek erişimi başlatıldı'],
  ])('renders %s as readable Turkish and keeps the raw code in technical details', (action, label) => {
    renderPlatform(<AuditTable rows={[entry(action, 1)]} emptyMessage="yok" />)
    expect(screen.getByText(label)).toBeVisible()
    expect(screen.getByText(action)).not.toBeVisible()
  })
})

describe('audit log rows', () => {
  it('shows when, what happened, who and which tenant; everything else is collapsed and copyable', async () => {
    const at = new Date(Date.now() - 3 * 3_600_000).toISOString()
    renderPlatform(<AuditTable rows={[entry('membership.role_changed', 1, { at, tenantId: 'tenant-a', tenantName: 'Acme A.Ş.', targetType: 'Membership', targetId: 'user-1', details: { role: 'Admin', changed: true, note: null } })]} emptyMessage="yok" />)
    expect(screen.getByText('3 saat önce')).toBeVisible()
    expect(screen.getByText('Üyelik rolü değiştirildi')).toBeVisible()
    expect(screen.getByText('Platform Operatörü')).toBeVisible()
    expect(screen.getByRole('link', { name: 'Acme A.Ş.' })).toHaveAttribute('href', '/platform/tenants/tenant-a')
    for (const raw of ['membership.role_changed', 'Membership', 'user-1', 'audit-1', 'role', 'Admin']) expect(screen.getByText(raw)).not.toBeVisible()
    expect(screen.getByText('PlatformAdmin')).not.toBeVisible()
    await userEvent.click(screen.getByText('Teknik ayrıntılar'))
    expect(screen.getByText('membership.role_changed')).toBeVisible()
    expect(screen.getByText('Admin')).toBeVisible()
    expect(screen.getByRole('button', { name: 'Kopyala: İşlem kodu' })).toBeVisible()
    expect(screen.getByRole('button', { name: 'Kopyala: role' })).toBeVisible()
  })

  it('says a code it cannot translate is an unknown action instead of printing the code in the body', () => {
    renderPlatform(<AuditTable rows={[entry('made.up_code', 1)]} emptyMessage="yok" />)
    expect(screen.getByText('Bilinmeyen işlem')).toBeVisible()
    expect(screen.getByText('made.up_code')).not.toBeVisible()
  })

  it('drops the tenant column where the tenant is the page', () => {
    renderPlatform(<AuditTable rows={[entry('plan.created', 1, { tenantId: 'tenant-a', tenantName: 'Acme A.Ş.' })]} showTenant={false} emptyMessage="yok" />)
    expect(screen.queryByRole('link', { name: 'Acme A.Ş.' })).not.toBeInTheDocument()
  })
})
