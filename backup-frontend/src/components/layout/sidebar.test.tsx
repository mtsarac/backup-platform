import { screen } from '@testing-library/react'
import { render } from '@/test/render'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Sidebar } from './sidebar'

const access = vi.hoisted(() => ({ tenantId: 'tenant-a', canAdmin: true }))
vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => access }))
vi.mock('@/features/auth/use-auth', () => ({ useAuth: () => ({ user: { isPlatformAdmin: false } }) }))

beforeEach(() => { access.canAdmin = true })

describe('Sidebar notifications entry', () => {
  it('links Bildirimler for Owner/Admin', () => {
    render(<MemoryRouter><Sidebar /></MemoryRouter>)
    expect(screen.getAllByRole('link', { name: 'Bildirimler' })[0]).toHaveAttribute('href', '/tenants/tenant-a/notifications')
  })

  it('hides Bildirimler from other roles', () => {
    access.canAdmin = false
    render(<MemoryRouter><Sidebar /></MemoryRouter>)
    expect(screen.queryByRole('link', { name: 'Bildirimler' })).not.toBeInTheDocument()
  })
})

describe('Sidebar discovery entry', () => {
  it('links Keşfedilen Veritabanları for every role', () => {
    access.canAdmin = false
    render(<MemoryRouter><Sidebar /></MemoryRouter>)
    expect(screen.getAllByRole('link', { name: 'Keşfedilen Veritabanları' })[0]).toHaveAttribute('href', '/tenants/tenant-a/discovery')
  })
})
