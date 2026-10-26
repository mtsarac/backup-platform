import { beforeEach, describe, expect, it, vi } from 'vitest'
import { screen, waitFor, within } from '@testing-library/react'
import { PlatformDashboardPage } from './platform-dashboard-page'
import * as api from '@/features/platform/api'
import * as storageApi from '@/features/platform/storage/api'
import { paged, renderPlatform } from '@/features/platform/test-utils'
import type { PlatformSummary } from '@/features/platform/types'

vi.mock('@/features/platform/api')
vi.mock('@/features/platform/storage/api')

const healthy: PlatformSummary = {
  tenants: 3, users: 7, enabledBackupJobs: 0, failedRunsLast24Hours: 0,
  agents: { total: 10, online: 8, offline: 0, unknown: 0, disabled: 0, revoked: 2 },
}

beforeEach(() => {
  vi.resetAllMocks()
  vi.mocked(api.getSummary).mockResolvedValue(healthy)
  vi.mocked(storageApi.getStorageUsage).mockResolvedValue(paged([]))
})

describe('platform dashboard status', () => {
  it('reports safe with no attention signals, without inventing rules for revoked computers or no enabled jobs', async () => {
    renderPlatform(<PlatformDashboardPage />)
    await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('Güvende'))
    expect(screen.queryByRole('list', { name: 'Dikkat gerektirenler' })).not.toBeInTheDocument()
  })

  it.each([
    ['failed runs', { ...healthy, failedRunsLast24Hours: 1 }, '/platform/audit'],
    ['offline computers', { ...healthy, agents: { ...healthy.agents, offline: 1 } }, '/platform/agents?connectivity=offline'],
    ['unknown connections', { ...healthy, agents: { ...healthy.agents, unknown: 1 } }, '/platform/agents?connectivity=unknown'],
    ['disabled computers', { ...healthy, agents: { ...healthy.agents, disabled: 1 } }, '/platform/agents?status=Disabled'],
  ] as const)('reports attention for %s alone and links to the existing destination', async (_rule, summary, href) => {
    vi.mocked(api.getSummary).mockResolvedValue(summary)
    renderPlatform(<PlatformDashboardPage />)
    await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('Dikkat'))
    const items = screen.getByRole('list', { name: 'Dikkat gerektirenler' })
    expect(within(items).getAllByRole('listitem')).toHaveLength(1)
    expect(within(items).getByRole('link')).toHaveAttribute('href', href)
  })

  it('limits linked attention items to three in the existing rule order', async () => {
    vi.mocked(api.getSummary).mockResolvedValue({ ...healthy, failedRunsLast24Hours: 4, agents: { ...healthy.agents, offline: 2, unknown: 1, disabled: 1 } })
    renderPlatform(<PlatformDashboardPage />)
    await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('Dikkat'))
    const items = screen.getByRole('list', { name: 'Dikkat gerektirenler' })
    expect(within(items).getAllByRole('listitem')).toHaveLength(3)
    expect(within(items).getAllByRole('link').map(link => link.getAttribute('href'))).toEqual([
      '/platform/audit', '/platform/agents?connectivity=offline', '/platform/agents?connectivity=unknown',
    ])
    expect(within(items).getAllByRole('link').every(link => link.textContent?.trim())).toBe(true)
  })
})
