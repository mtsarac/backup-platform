import { screen, within } from '@testing-library/react'
import { render } from '@/test/render'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { describe, expect, it, vi } from 'vitest'
import { BackupCenterLayout } from './backup-center-layout'

vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => ({ tenantId: 'tenant-a' }) }))

function mount(path: string) {
  render(<MemoryRouter initialEntries={[path]}><Routes>
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
  </Routes></MemoryRouter>)
}

describe('Backup Center layout', () => {
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
})
