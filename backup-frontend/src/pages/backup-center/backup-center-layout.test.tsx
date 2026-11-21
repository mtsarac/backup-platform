import { screen, waitFor, within } from '@testing-library/react'
import { render } from '@/test/render'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { getBackupJobs } from '@/features/backup-jobs/api'
import { savedJob } from '@/features/backup-jobs/test-fixtures'
import { BackupCenterLayout } from './backup-center-layout'

vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => ({ tenantId: 'tenant-a' }) }))
vi.mock('@/features/backup-jobs/api')

function mount(path: string) {
  const cache = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  render(<QueryClientProvider client={cache}><MemoryRouter initialEntries={[path]}><Routes>
    <Route path="/tenants/:tenantId">
      <Route element={<BackupCenterLayout />}>
        <Route path="backup-jobs" element={<p>jobs list</p>} />
        <Route path="backup-runs" element={<p>runs list</p>} />
        <Route path="backup-runs/:id" element={<p>run detail</p>} />
        <Route path="restores" element={<p>restores list</p>} />
      </Route>
      <Route path="backup-jobs/new" element={<p>job wizard</p>} />
      <Route path="backup-runs/:id/restore" element={<p>run restore</p>} />
    </Route>
  </Routes></MemoryRouter></QueryClientProvider>)
}

describe('Backup Center layout', () => {
  beforeEach(() => { vi.mocked(getBackupJobs).mockResolvedValue([savedJob]) })

  it('shows one page heading and the four lists as navigation links, the open one marked current', () => {
    mount('/tenants/tenant-a/backup-runs')
    expect(screen.getAllByRole('heading', { level: 1 })).toHaveLength(1)
    const tabs = within(screen.getByRole('navigation', { name: 'Yedekleme bölümleri' }))
    expect(tabs.getAllByRole('link').map((link) => [link.textContent, link.getAttribute('href')])).toEqual([
      ['Yedekleme işleri', '/tenants/tenant-a/backup-jobs'],
      ['Geçmiş', '/tenants/tenant-a/backup-runs'],
      ['Geri yüklemeler', '/tenants/tenant-a/restores'],
      ['Paylaşımlar', '/tenants/tenant-a/backup-shares'],
    ])
    expect(tabs.getByRole('link', { current: 'page' })).toHaveTextContent('Geçmiş')
    expect(screen.getByText('runs list')).toBeInTheDocument()
  })

  it('switches lists by navigation, and wizard and detail routes stay outside the tabs', async () => {
    mount('/tenants/tenant-a/backup-jobs')
    await userEvent.click(screen.getByRole('link', { name: 'Geri yüklemeler' }))
    expect(screen.getByText('restores list')).toBeInTheDocument()
    expect(screen.queryByText('jobs list')).not.toBeInTheDocument()
  })

  it.each(['/tenants/tenant-a/backup-jobs/new', '/tenants/tenant-a/backup-runs/run-1/restore'])('%s is not wrapped in the tabs', (path) => {
    mount(path)
    expect(screen.queryByRole('navigation', { name: 'Yedekleme bölümleri' })).not.toBeInTheDocument()
  })

  it('puts the create-job button at the top right of the heading on the jobs list, once there are jobs', async () => {
    mount('/tenants/tenant-a/backup-jobs')
    expect(await screen.findByRole('link', { name: 'Yedekleme işi oluştur' })).toHaveAttribute('href', '/tenants/tenant-a/backup-jobs/new')
    expect(screen.getByRole('banner').contains(screen.getByRole('link', { name: 'Yedekleme işi oluştur' }))).toBe(true)
  })

  it('leaves the create-job button to the empty list when there are no jobs, and off the other tabs', async () => {
    vi.mocked(getBackupJobs).mockResolvedValue([])
    mount('/tenants/tenant-a/backup-jobs')
    await waitFor(() => expect(getBackupJobs).toHaveBeenCalled())
    expect(screen.queryByRole('link', { name: 'Yedekleme işi oluştur' })).not.toBeInTheDocument()
  })

  it('shows no create-job button on the other tabs', async () => {
    mount('/tenants/tenant-a/backup-runs')
    expect(await screen.findByText('runs list')).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'Yedekleme işi oluştur' })).not.toBeInTheDocument()
    expect(getBackupJobs).not.toHaveBeenCalled()
  })
})
