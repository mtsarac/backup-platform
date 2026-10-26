import { screen } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import * as api from '@/features/platform/api'
import { paged, renderPlatform } from '@/features/platform/test-utils'
import { PlatformAuditPage } from './platform-audit-page'

vi.mock('@/features/platform/api')

beforeEach(() => {
  vi.resetAllMocks()
  vi.mocked(api.getTenants).mockResolvedValue(paged([]))
})

describe('platform audit page', () => {
  it('lists plain action labels with the raw codes collapsed', async () => {
    vi.mocked(api.getAudit).mockResolvedValue(paged([{ id: 'audit-1', at: new Date().toISOString(), actorUserId: 'admin-id', actorDisplayName: 'Platform Operatörü', actorKind: 'PlatformAdmin', action: 'break_glass.approved', tenantId: 'tenant-a', tenantName: 'Acme A.Ş.', targetType: 'break_glass_request', targetId: 'request-1', details: {} }]))
    renderPlatform(<PlatformAuditPage />)
    expect(await screen.findByText('Acil destek erişimi onaylandı')).toBeVisible()
    expect(screen.getByText('break_glass.approved')).not.toBeVisible()
    expect(screen.getByText('request-1')).not.toBeVisible()
  })

  it('says when there is nothing to show', async () => {
    vi.mocked(api.getAudit).mockResolvedValue(paged([]))
    renderPlatform(<PlatformAuditPage />)
    expect(await screen.findByText('Henüz denetim kaydı yok.')).toBeVisible()
  })
})
