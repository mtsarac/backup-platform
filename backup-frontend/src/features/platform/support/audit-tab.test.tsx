import { screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import * as api from './api'
import { SupportAuditTab } from './audit-tab'
import { paged, renderPlatform } from '../test-utils'
import type { SupportSession } from './types'

vi.mock('./api')

describe('support session audit tab', () => {
  it('uses the shared plain action labels and keeps the raw code and detail pairs collapsed', async () => {
    vi.mocked(api.getSessionAudit).mockResolvedValue(paged([{ id: 'audit-a', at: new Date().toISOString(), action: 'support.restore.requested', actorUserId: 'admin-id', actorDisplayName: 'Platform Operatörü', targetType: 'restore', targetId: 'restore-1', result: 'Success', details: { database: 'Muhasebe' } }]))
    renderPlatform(<SupportAuditTab session={{ id: 'support-a', tenantId: 'tenant-a' } as SupportSession} />)
    expect(await screen.findByText('Geri yükleme istendi')).toBeVisible()
    for (const raw of ['support.restore.requested', 'restore-1', 'database', 'Muhasebe']) expect(screen.getByText(raw)).not.toBeVisible()
  })
})
