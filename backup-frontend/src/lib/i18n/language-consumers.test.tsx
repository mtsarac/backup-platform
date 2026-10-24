import { describe, expect, it, vi } from 'vitest'
import { fireEvent, screen } from '@testing-library/react'
import { render } from '@/test/render'
import { MemoryRouter } from 'react-router-dom'
import { LoginPage } from '@/pages/login/login-page'
import { TenantSelectPage } from '@/pages/tenancy/tenant-select-page'
import { formatGb, toGbInput, parseGb } from '@/features/platform/plans/storage'
import { formatDateTime } from '@/lib/format'

vi.mock('@/features/auth/use-auth', () => ({ useAuth: () => ({
  login: vi.fn(), logout: vi.fn(), isAuthenticated: false, sessionExpired: false,
  user: { displayName: 'Customer Agent Tenant', isPlatformAdmin: false },
  tenants: [{ tenantId: 'one', name: 'Customer Agent Tenant', role: 'Owner' }, { tenantId: 'two', name: 'English Customer Name', role: 'Viewer' }],
}) }))

describe('Language consumers', () => {
  it('switches complete login copy without remounting or translating entered values, and localizes formatters', () => {
    render(<MemoryRouter><LoginPage /></MemoryRouter>)
    fireEvent.change(screen.getByRole('textbox', { name: 'Kullanıcı adı' }), { target: { value: 'Customer Agent Tenant' } })
    expect(screen.getByText('Her yedekleme doğrulanır ve sağlama toplamıyla kayıt altına alınır.')).toBeInTheDocument()
    expect(formatGb(2_500_000_000)).toBe('2,5 GB')
    fireEvent.click(screen.getByRole('button', { name: 'Dili değiştir' }))
    expect(screen.getByRole('heading', { name: 'Sign in' })).toBeInTheDocument()
    expect(screen.getByRole('textbox', { name: 'Username' })).toHaveValue('Customer Agent Tenant')
    expect(screen.getByLabelText('Password')).toBeInTheDocument()
    expect(screen.getByText('Every backup is verified and recorded with a checksum.')).toBeInTheDocument()
    expect(screen.queryByText('Her yedekleme doğrulanır ve sağlama toplamıyla kayıt altına alınır.')).not.toBeInTheDocument()
    expect(localStorage.getItem('bp_language')).toBe('en')
    expect(document.documentElement.lang).toBe('en')
    expect(formatGb(2_500_000_000)).toBe('2.5 GB')
    expect(parseGb(toGbInput(2_500_000_000))).toEqual({ bytes: 2_500_000_000 })
    const date = '2026-10-16T12:15:00Z'
    expect(formatDateTime(date)).toBe(new Intl.DateTimeFormat('en-US', { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(date)))
  })

  it('localizes workspace roles while preserving customer names and restores the saved preference', () => {
    localStorage.setItem('bp_language', 'en')
    render(<MemoryRouter><TenantSelectPage /></MemoryRouter>)
    expect(screen.getByText('Customer Agent Tenant')).toBeInTheDocument()
    expect(screen.getByText('Owner')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Change language' }))
    expect(screen.getByText('Sahip')).toBeInTheDocument()
    expect(screen.getByText('Görüntüleyici')).toBeInTheDocument()
    expect(screen.getByText('Customer Agent Tenant')).toBeInTheDocument()
    expect(screen.getByText('English Customer Name')).toBeInTheDocument()
    expect(document.documentElement.lang).toBe('tr')
  })
})
