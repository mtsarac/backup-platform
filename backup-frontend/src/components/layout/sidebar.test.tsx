import { act, fireEvent, screen, within } from '@testing-library/react'
import { render } from '@/test/render'
import { MemoryRouter, Route, Routes, createRoutesFromChildren, matchRoutes } from 'react-router-dom'
import { BackupCenterLayout } from '@/pages/backup-center/backup-center-layout'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { changeLanguage } from '@/lib/i18n/runtime'
import { tenantRoutes } from '@/route-tables'
import { AppShell } from './app-shell'
import { Sidebar } from './sidebar'

const access = vi.hoisted(() => ({ tenantId: 'tenant-a', canAdmin: true, isPlatformAdmin: false }))
vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => ({ ...access, tenant: { tenantId: 'tenant-a', name: 'Acme' }, switchTenant: vi.fn() }) }))
vi.mock('@/features/auth/use-auth', () => ({ useAuth: () => ({ user: { role: access.isPlatformAdmin ? 'PlatformAdmin' : 'UserAdmin', displayName: 'Ada' }, tenants: [{ tenantId: 'tenant-a', name: 'Acme' }], logout: vi.fn() }) }))

vi.mock('@/hooks/use-theme', () => ({ useTheme: () => ({ resolvedTheme: 'light', toggleTheme: vi.fn() }) }))

beforeEach(() => { access.canAdmin = true; access.isPlatformAdmin = false })

const base = '/tenants/tenant-a'
const nav = () => screen.getAllByRole('navigation')[0]
const group = (name: string) => within(nav()).getByRole('group', { name })
const links = (name: string) => within(group(name)).getAllByRole('link').map((link) => [link.textContent, link.getAttribute('href')])

describe('Tenant sidebar', () => {
  it('groups every existing page under four groups with unchanged URLs', () => {
    render(<MemoryRouter><Sidebar /></MemoryRouter>)
    expect(within(nav()).getByRole('link', { name: 'Genel Bakış' })).toHaveAttribute('href', base)
    expect(within(nav()).getByRole('link', { name: 'Yedeklemeler' })).toHaveAttribute('href', `${base}/backup-jobs`)
    expect(within(nav()).getByRole('link', { name: 'Paylaşımlarım' })).toHaveAttribute('href', `${base}/shares`)
    expect(within(nav()).queryByRole('link', { name: 'Yedekleme işleri' })).not.toBeInTheDocument()
    expect(links('Altyapı')).toEqual([
      ['Yedekleme bilgisayarları', `${base}/agents`],
      ['Veritabanı bulma', `${base}/discovery`],
      ['Depolama', `${base}/storage`],
    ])
    expect(links('Ayarlar')).toEqual([
      ['Bildirimler', `${base}/notifications`],
      ['Şifreleme', `${base}/encryption`],
      ['Yükleme bant genişliği', `${base}/upload-bandwidth`],
    ])
    expect(within(nav()).queryByRole('link', { name: 'Platform Yönetimi' })).not.toBeInTheDocument()
  })

  it('keeps only upload bandwidth in Ayarlar for non-administrators', () => {
    access.canAdmin = false
    render(<MemoryRouter><Sidebar /></MemoryRouter>)
    expect(links('Ayarlar')).toEqual([['Yükleme bant genişliği', `${base}/upload-bandwidth`]])
    expect(links('Altyapı')).toHaveLength(3)
  })

  it('links to Platform Yönetimi only for platform administrators', () => {
    access.isPlatformAdmin = true
    render(<MemoryRouter><Sidebar /></MemoryRouter>)
    expect(within(nav()).getByRole('link', { name: 'Platform Yönetimi' })).toHaveAttribute('href', '/platform')
  })

  it('never links to a page that has no route', () => {
    access.isPlatformAdmin = true
    render(<MemoryRouter><Sidebar /></MemoryRouter>)
    const routes = createRoutesFromChildren(tenantRoutes)
    const targets = within(nav()).getAllByRole('link').map((link) => link.getAttribute('href')!).filter((href) => href.startsWith(base))
    expect(targets).toHaveLength(9)
    for (const href of targets) expect(matchRoutes(routes, href.slice(base.length) || '/')?.at(-1)?.route.path, href).not.toBe('*')
  })

  it('shows the same menu in English without leftover Turkish labels', () => {
    render(<MemoryRouter><Sidebar /></MemoryRouter>)
    act(() => changeLanguage('en'))
    expect(within(nav()).getByRole('link', { name: 'Backups' })).toBeInTheDocument()
    expect(within(nav()).getByRole('link', { name: 'My shares' })).toBeInTheDocument()
    expect(links('Infrastructure').map(([label]) => label)).toEqual(['Backup computers', 'Database discovery', 'Storage'])
    expect(links('Settings').map(([label]) => label)).toEqual(['Notifications', 'Encryption', 'Upload bandwidth'])
    expect(within(nav()).getByRole('link', { name: 'Overview' })).toBeInTheDocument()
  })

  it('opens the same menu in a drawer from the top bar on phones', () => {
    const showModal = vi.fn(function (this: HTMLDialogElement) { this.setAttribute('open', '') })
    Object.defineProperty(HTMLDialogElement.prototype, 'showModal', { configurable: true, value: showModal })
    render(<MemoryRouter><AppShell>{null}</AppShell></MemoryRouter>)
    fireEvent.click(screen.getByRole('button', { name: 'Menüyü aç' }))
    const drawer = screen.getByRole('dialog', { name: 'Ana menü' })
    expect(showModal).toHaveBeenCalled()
    expect(within(drawer).getByRole('link', { name: 'Yedeklemeler' })).toHaveAttribute('href', `${base}/backup-jobs`)
    expect(within(drawer).getByRole('link', { name: /Ada/ })).toHaveAttribute('href', '/account')
    expect(within(drawer).getByRole('button', { name: 'Menüyü kapat' })).toBeInTheDocument()
  })

  it.each([
    'backup-jobs', 'backup-jobs/new', 'backup-jobs/j1/edit', 'backup-runs', 'backup-runs/r1', 'artifacts', 'artifacts/a1', 'artifacts/a1/restore', 'restores', 'restores/x1',
  ])('marks the single Yedeklemeler entry current on %s', (path) => {
    render(<MemoryRouter initialEntries={[`${base}/${path}`]}><Sidebar /></MemoryRouter>)
    expect(within(nav()).getAllByRole('link', { current: 'page' }).map((link) => link.textContent)).toEqual(['Yedeklemeler'])
  })

  it('leaves Yedeklemeler inactive on Paylaşımlarım and the dashboard', () => {
    render(<MemoryRouter initialEntries={[`${base}/shares`]}><Sidebar /></MemoryRouter>)
    expect(within(nav()).getAllByRole('link', { current: 'page' }).map((link) => link.textContent)).toEqual(['Paylaşımlarım'])
  })

  it('does not repeat any backup center tab name as a sidebar entry', () => {
    render(<MemoryRouter><Sidebar /></MemoryRouter>)
    const names = within(nav()).getAllByRole('link').map((link) => link.textContent)
    render(<MemoryRouter initialEntries={[`${base}/backup-jobs`]}><Routes><Route path="/tenants/:tenantId" element={<BackupCenterLayout />}><Route path="backup-jobs" element={null} /></Route></Routes></MemoryRouter>)
    const tabs = within(screen.getByRole('navigation', { name: 'Yedekleme bölümleri' })).getAllByRole('link').map((link) => link.textContent)
    expect(tabs).toHaveLength(4)
    for (const tab of tabs) expect(names).not.toContain(tab)
  })

  it('pins the account settings entry with the display name', () => {
    render(<MemoryRouter><Sidebar /></MemoryRouter>)
    const entry = within(nav().closest('aside')!).getByRole('link', { name: /Ada/ })
    expect(entry).toHaveAttribute('href', '/account')
    expect(entry).toHaveTextContent('Ayarlar')
  })
})
