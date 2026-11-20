import { screen, within } from '@testing-library/react'
import { render } from '@/test/render'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { describe, expect, it, vi } from 'vitest'
import { SettingsLayout } from './settings-layout'

vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => ({ tenantId: 'tenant-a' }) }))


function mount(path: string) {
  render(<MemoryRouter initialEntries={[path]}><Routes>
    <Route path="/tenants/:tenantId/settings" element={<SettingsLayout />}>
      <Route index element={<p>notifications section</p>} />
      <Route path="recovery-key" element={<p>recovery key section</p>} />
      <Route path="bandwidth" element={<p>bandwidth section</p>} />
    </Route>
  </Routes></MemoryRouter>)
  return within(screen.getByRole('navigation', { name: 'Ayar bölümleri' }))
}

describe('Settings layout', () => {
  it('shows one heading and the three sections as tabs, the open one marked current', () => {
    const tabs = mount('/tenants/tenant-a/settings/recovery-key')
    expect(screen.getByRole('heading', { level: 1, name: 'Ayarlar' })).toBeInTheDocument()
    expect(tabs.getAllByRole('link').map((link) => [link.textContent, link.getAttribute('href')])).toEqual([
      ['Bildirimler', '/tenants/tenant-a/settings'],
      ['Kurtarma anahtarı', '/tenants/tenant-a/settings/recovery-key'],
      ['Yükleme bant genişliği', '/tenants/tenant-a/settings/bandwidth'],
    ])
    expect(tabs.getByRole('link', { current: 'page' })).toHaveTextContent('Kurtarma anahtarı')
    expect(screen.getByText('recovery key section')).toBeInTheDocument()
  })
})
