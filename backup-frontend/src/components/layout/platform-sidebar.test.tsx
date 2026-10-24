import { act, fireEvent, screen, within } from '@testing-library/react'
import { MemoryRouter, Route } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { render } from '@/test/render'
import { ThemeProvider } from '@/components/theme-provider'
import { renderPlatform } from '@/features/platform/test-utils'
import { changeLanguage } from '@/lib/i18n/runtime'
import { PlatformShell } from './platform-shell'
import { PlatformSidebar } from './platform-sidebar'

const nav = () => screen.getAllByRole('navigation')[0]
const group = (name: string) => within(nav()).getByRole('group', { name })
const links = (name: string) => within(group(name)).getAllByRole('link').map((link) => [link.textContent, link.getAttribute('href')])

beforeEach(() => localStorage.setItem('backup-theme', 'light'))

describe('Platform sidebar', () => {
  it('groups every platform page under the four plain-language headings with unchanged URLs', () => {
    render(<MemoryRouter><PlatformSidebar /></MemoryRouter>)
    expect(links('Genel bakış')).toEqual([['Platform özeti', '/platform']])
    expect(links('Müşteriler')).toEqual([
      ['Çalışma alanları', '/platform/tenants'],
      ['Kullanıcılar', '/platform/users'],
      ['Planlar', '/platform/plans'],
    ])
    expect(links('Altyapı')).toEqual([
      ['Depolama kullanımı', '/platform/storage'],
      ['Depolama hedefleri', '/platform/storage-connections'],
      ['Yedekleme bilgisayarları', '/platform/agents'],
      ['Kota ilkeleri', '/platform/quota-policies'],
    ])
    expect(links('Güvenlik ve destek')).toEqual([
      ['Destek oturumları', '/platform/support-sessions'],
      ['Acil destek erişimi', '/platform/break-glass'],
      ['Denetim kaydı', '/platform/audit'],
      ['Bildirim altyapısı', '/platform/notification-transports'],
    ])
    expect(within(nav()).getByRole('link', { name: 'Çalışma alanı arayüzüne dön' })).toHaveAttribute('href', '/')
  })

  it('uses plain words only: no English or break-glass jargon in either language', () => {
    render(<MemoryRouter><PlatformSidebar /></MemoryRouter>)
    const labels = () => within(nav()).getAllByRole('link').map((link) => link.textContent ?? '')
    expect(labels().join(' ')).not.toMatch(/break-glass|ajan|tenant|audit/i)
    act(() => changeLanguage('en'))
    expect(links('Overview').map(([label]) => label)).toEqual(['Platform overview'])
    expect(links('Customers').map(([label]) => label)).toEqual(['Workspaces', 'Users', 'Plans'])
    expect(links('Infrastructure').map(([label]) => label)).toEqual(['Storage usage', 'Storage destinations', 'Backup computers', 'Quota policies'])
    expect(links('Security and support').map(([label]) => label)).toEqual(['Support sessions', 'Emergency support access', 'Audit log', 'Notification infrastructure'])
    expect(labels().join(' ')).not.toMatch(/break-glass|agent\b/i)
  })

  it('opens the same menu in a drawer from the top bar on phones', async () => {
    const showModal = vi.fn(function (this: HTMLDialogElement) { this.setAttribute('open', '') })
    Object.defineProperty(HTMLDialogElement.prototype, 'showModal', { configurable: true, value: showModal })
    renderPlatform(null, { routes: <Route element={<ThemeProvider><PlatformShell /></ThemeProvider>}><Route path="*" element={null} /></Route> })
    fireEvent.click(await screen.findByRole('button', { name: 'Menüyü aç' }))
    const drawer = screen.getByRole('dialog', { name: 'Platform menüsü' })
    expect(showModal).toHaveBeenCalled()
    expect(within(drawer).getByRole('link', { name: 'Denetim kaydı' })).toHaveAttribute('href', '/platform/audit')
    expect(within(drawer).getByRole('button', { name: 'Menüyü kapat' })).toBeInTheDocument()
  })
})
