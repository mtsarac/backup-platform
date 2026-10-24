import { act, fireEvent, screen, within } from '@testing-library/react'
import { render } from '@/test/render'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { changeLanguage } from '@/lib/i18n/runtime'
import { AppShell } from './app-shell'
import { Sidebar } from './sidebar'

const access = vi.hoisted(() => ({ tenantId: 'tenant-a', canAdmin: true }))
vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => ({ ...access, tenant: { role: 'Owner' }, switchTenant: vi.fn() }) }))
vi.mock('@/features/auth/use-auth', () => ({ useAuth: () => ({ user: { isPlatformAdmin: false, displayName: 'Ada' }, tenants: [{ tenantId: 'tenant-a', name: 'Acme' }], logout: vi.fn() }) }))

vi.mock('@/hooks/use-theme', () => ({ useTheme: () => ({ resolvedTheme: 'light', toggleTheme: vi.fn() }) }))

beforeEach(() => { access.canAdmin = true })

const base = '/tenants/tenant-a'
const nav = () => screen.getAllByRole('navigation')[0]
const group = (name: string) => within(nav()).getByRole('group', { name })
const links = (name: string) => within(group(name)).getAllByRole('link').map((link) => [link.textContent, link.getAttribute('href')])

describe('Tenant sidebar', () => {
  it('groups every existing page under the four plain-language headings with unchanged URLs', () => {
    render(<MemoryRouter><Sidebar /></MemoryRouter>)
    expect(within(nav()).getByRole('link', { name: 'Ana Sayfa' })).toHaveAttribute('href', base)
    expect(links('Yedeklerim')).toEqual([
      ['Yedekleme işleri', `${base}/backup-jobs`],
      ['Yedekleme denemeleri', `${base}/backup-runs`],
      ['Yedek dosyaları', `${base}/artifacts`],
      ['Geri yüklemeler', `${base}/restores`],
    ])
    expect(links('Bilgisayarlarım ve Depolama')).toEqual([
      ['Yedekleme bilgisayarları', `${base}/agents`],
      ['Veritabanı bulma', `${base}/discovery`],
      ['Depolama alanları', `${base}/storage`],
      ['Yükleme bant genişliği', `${base}/upload-bandwidth`],
    ])
    expect(links('Ayarlar')).toEqual([
      ['Bildirimler', `${base}/notifications`],
      ['Şifreleme', `${base}/encryption`],
      ['Destek erişimi', `${base}/support-access`],
    ])
  })

  it('hides the whole Ayarlar group from non-administrators but keeps the other pages', () => {
    access.canAdmin = false
    render(<MemoryRouter><Sidebar /></MemoryRouter>)
    expect(within(nav()).queryByRole('group', { name: 'Ayarlar' })).not.toBeInTheDocument()
    for (const label of ['Bildirimler', 'Şifreleme', 'Destek erişimi']) expect(within(nav()).queryByRole('link', { name: label })).not.toBeInTheDocument()
    expect(links('Yedeklerim')).toHaveLength(4)
    expect(links('Bilgisayarlarım ve Depolama')).toHaveLength(4)
  })

  it('shows the same menu in English without leftover Turkish labels', () => {
    render(<MemoryRouter><Sidebar /></MemoryRouter>)
    act(() => changeLanguage('en'))
    expect(links('My backups').map(([label]) => label)).toEqual(['Backup jobs', 'Backup runs', 'Backup files', 'Restores'])
    expect(links('My computers and storage').map(([label]) => label)).toEqual(['Backup computers', 'Database discovery', 'Storage locations', 'Upload bandwidth'])
    expect(links('Settings').map(([label]) => label)).toEqual(['Notifications', 'Encryption', 'Support access'])
    expect(within(nav()).getByRole('link', { name: 'Home' })).toBeInTheDocument()
  })

  it('opens the same menu in a drawer from the top bar on phones', () => {
    const showModal = vi.fn(function (this: HTMLDialogElement) { this.setAttribute('open', '') })
    Object.defineProperty(HTMLDialogElement.prototype, 'showModal', { configurable: true, value: showModal })
    render(<MemoryRouter><AppShell>{null}</AppShell></MemoryRouter>)
    fireEvent.click(screen.getByRole('button', { name: 'Menüyü aç' }))
    const drawer = screen.getByRole('dialog', { name: 'Ana menü' })
    expect(showModal).toHaveBeenCalled()
    expect(within(drawer).getByRole('link', { name: 'Yedekleme işleri' })).toHaveAttribute('href', `${base}/backup-jobs`)
    expect(within(drawer).getByRole('button', { name: 'Menüyü kapat' })).toBeInTheDocument()
  })
})
