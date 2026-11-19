import { act, fireEvent, screen, within } from '@testing-library/react'
import { render } from '@/test/render'
import { MemoryRouter, Route, Routes, createRoutesFromChildren, matchRoutes } from 'react-router-dom'
import { BackupCenterLayout } from '@/pages/backup-center/backup-center-layout'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { changeLanguage } from '@/lib/i18n/runtime'
import { tenantRoutes } from '@/route-tables'
import { AppShell } from './app-shell'
import { Sidebar } from './sidebar'

const access = vi.hoisted(() => ({ tenantId: 'tenant-a', isPlatformAdmin: false }))
vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => ({ ...access, tenant: { tenantId: 'tenant-a', name: 'Acme' }, switchTenant: vi.fn() }) }))
vi.mock('@/features/auth/use-auth', () => ({ useAuth: () => ({ user: { role: access.isPlatformAdmin ? 'PlatformAdmin' : 'UserAdmin', displayName: 'Ada' }, tenants: [{ tenantId: 'tenant-a', name: 'Acme' }], logout: vi.fn() }) }))

vi.mock('@/hooks/use-theme', () => ({ useTheme: () => ({ resolvedTheme: 'light', toggleTheme: vi.fn() }) }))

beforeEach(() => { access.isPlatformAdmin = false })

const base = '/tenants/tenant-a'
const nav = () => screen.getAllByRole('navigation')[0]

describe('Tenant sidebar', () => {
  it('shows exactly five headingless items with their URLs', () => {
    render(<MemoryRouter><Sidebar /></MemoryRouter>)
    expect(within(nav()).queryAllByRole('group')).toHaveLength(0)
    expect(within(nav()).getAllByRole('link').map((link) => [link.textContent, link.getAttribute('href')])).toEqual([
      ['Ana sayfa', base],
      ['Yedeklemeler', `${base}/backup-jobs`],
      ['Bilgisayarlar', `${base}/agents`],
      ['Depolama', `${base}/storage`],
      ['Ayarlar', `${base}/settings`],
    ])
    expect(within(nav().closest('aside')!).queryByRole('link', { name: 'Platform Yönetimi' })).not.toBeInTheDocument()
  })

  it('pins Platform Yönetimi below the menu, only for platform administrators', () => {
    access.isPlatformAdmin = true
    render(<MemoryRouter><Sidebar /></MemoryRouter>)
    expect(within(nav()).getAllByRole('link')).toHaveLength(5)
    expect(within(nav().closest('aside')!).getByRole('link', { name: 'Platform Yönetimi' })).toHaveAttribute('href', '/platform')
  })

  it('never links to a page that has no route', () => {
    render(<MemoryRouter><Sidebar /></MemoryRouter>)
    const routes = createRoutesFromChildren(tenantRoutes)
    for (const link of within(nav()).getAllByRole('link')) {
      const href = link.getAttribute('href')!
      expect(matchRoutes(routes, href.slice(base.length) || '/')?.at(-1)?.route.path, href).not.toBe('*')
    }
  })

  it.each(['shares', 'discovery', 'notifications', 'encryption', 'upload-bandwidth', 'artifacts', 'artifacts/a1', 'artifacts/a1/restore', 'backup-jobs/j1/edit'])('no longer resolves %s', (path) => {
    expect(matchRoutes(createRoutesFromChildren(tenantRoutes), `/${path}`)?.at(-1)?.route.path).toBe('*')
  })

  it.each(['backup-shares', 'agents/databases', 'settings', 'settings/recovery-key', 'settings/bandwidth', 'discovery/instances/i1', 'backup-jobs/j1', 'backup-runs/r1/restore'])('resolves the new location %s', (path) => {
    expect(matchRoutes(createRoutesFromChildren(tenantRoutes), `/${path}`)?.at(-1)?.route.path).not.toBe('*')
  })

  it('shows the same menu in English without leftover Turkish labels', () => {
    render(<MemoryRouter><Sidebar /></MemoryRouter>)
    act(() => changeLanguage('en'))
    expect(within(nav()).getAllByRole('link').map((link) => link.textContent)).toEqual(['Home', 'Backups', 'Computers', 'Storage', 'Settings'])
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
    'backup-jobs', 'backup-jobs/new', 'backup-jobs/j1', 'backup-runs', 'backup-runs/r1', 'backup-runs/r1/restore', 'restores', 'restores/x1', 'backup-shares',
  ])('marks the single Yedeklemeler entry current on %s', (path) => {
    render(<MemoryRouter initialEntries={[`${base}/${path}`]}><Sidebar /></MemoryRouter>)
    expect(within(nav()).getAllByRole('link', { current: 'page' }).map((link) => link.textContent)).toEqual(['Yedeklemeler'])
  })

  it.each([
    ['Ana sayfa', ''], ['Bilgisayarlar', '/agents'], ['Bilgisayarlar', '/agents/databases'], ['Bilgisayarlar', '/agents/a1'], ['Bilgisayarlar', '/enrollment'], ['Bilgisayarlar', '/discovery/instances/i1'],
    ['Depolama', '/storage'], ['Depolama', '/storage/connections/c1'], ['Ayarlar', '/settings'], ['Ayarlar', '/settings/recovery-key'], ['Ayarlar', '/settings/bandwidth'],
  ])('marks only %s current on %s', (label, path) => {
    render(<MemoryRouter initialEntries={[`${base}${path}`]}><Sidebar /></MemoryRouter>)
    expect(within(nav()).getAllByRole('link', { current: 'page' }).map((link) => link.textContent)).toEqual([label])
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
