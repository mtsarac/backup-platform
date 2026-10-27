import { beforeEach, describe, expect, it, vi } from 'vitest'
import { screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { Outlet, Route } from 'react-router-dom'
import { PlatformSidebar } from '@/components/layout/platform-sidebar'
import * as platformApi from '@/features/platform/api'
import * as storageApi from '@/features/platform/storage/api'
import * as connectionApi from '@/features/storage-connections/api'
import { paged, renderPlatform } from '@/features/platform/test-utils'
import { platformRoutes } from '@/route-tables'

vi.mock('@/features/platform/api')
vi.mock('@/features/platform/storage/api')
vi.mock('@/features/storage-connections/api')

const tree = <Route path="/platform" element={<><PlatformSidebar /><Outlet /></>}>{platformRoutes}</Route>
const menuEntry = () => screen.getAllByRole('link', { name: 'Depolama' })[0]

beforeEach(() => {
  vi.resetAllMocks()
  vi.mocked(storageApi.getStorageUsage).mockResolvedValue(paged([]))
  vi.mocked(connectionApi.getPlatformConnections).mockResolvedValue(paged([]))
  vi.mocked(connectionApi.getPlatformProviders).mockResolvedValue({ items: [] })
  vi.mocked(platformApi.getTenants).mockResolvedValue(paged([]))
})

describe('platform storage hub', () => {
  it('offers usage and destinations as tabs; the Storage menu entry stays current on both', async () => {
    renderPlatform(null, { path: '/platform/storage', routes: tree })
    const tabs = await screen.findByRole('navigation', { name: 'Depolama bölümleri' })
    expect(screen.getByRole('heading', { level: 1, name: 'Depolama' })).toBeInTheDocument()
    expect(within(tabs).getByRole('link', { name: 'Kullanım' })).toHaveAttribute('aria-current', 'page')
    expect(menuEntry()).toHaveAttribute('aria-current', 'page')
    await userEvent.setup().click(within(tabs).getByRole('link', { name: 'Hedefler' }))
    expect(await screen.findByRole('searchbox', { name: 'Çalışma alanı ara' })).toBeInTheDocument()
    expect(screen.getByTestId('location')).toHaveTextContent('/platform/storage/connections')
    expect(within(tabs).getByRole('link', { name: 'Hedefler' })).toHaveAttribute('aria-current', 'page')
    expect(menuEntry()).toHaveAttribute('aria-current', 'page')
  })

  it('keeps the old destinations URL and its filters working', async () => {
    renderPlatform(null, { path: '/platform/storage-connections?ownership=all&enabled=false', routes: tree })
    expect(await screen.findByRole('searchbox', { name: 'Çalışma alanı ara' })).toBeInTheDocument()
    expect(screen.getByTestId('location')).toHaveTextContent('/platform/storage/connections?ownership=all&enabled=false')
    expect(connectionApi.getPlatformConnections).toHaveBeenCalledWith(expect.objectContaining({ ownership: undefined, enabled: false }), expect.any(AbortSignal))
  })
})
