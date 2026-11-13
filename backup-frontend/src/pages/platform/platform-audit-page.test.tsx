import { screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
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
    vi.mocked(api.getAudit).mockResolvedValue(paged([{ id: 'audit-1', at: new Date().toISOString(), actorUserId: 'admin-id', actorDisplayName: 'Platform Operatörü', actorKind: 'PlatformAdmin', action: 'tenant_wrap_key.rotated', tenantId: 'tenant-a', tenantName: 'Acme A.Ş.', targetType: 'tenant_wrap_key', targetId: 'request-1', details: {} }]))
    renderPlatform(<PlatformAuditPage />)
    expect(await screen.findByText('Şifreleme anahtarı yenilendi')).toBeVisible()
    expect(screen.queryByText('tenant_wrap_key.rotated')).not.toBeInTheDocument()
    expect(screen.queryByText('request-1')).not.toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Teknik ayrıntılar' }))
    const dialog = within(await screen.findByRole('dialog'))
    expect(dialog.getByText('tenant_wrap_key.rotated')).toBeVisible()
    expect(dialog.getByText('request-1')).toBeVisible()
  })

  it('says when there is nothing to show', async () => {
    vi.mocked(api.getAudit).mockResolvedValue(paged([]))
    renderPlatform(<PlatformAuditPage />)
    expect(await screen.findByText('Henüz denetim kaydı yok.')).toBeVisible()
  })
})
