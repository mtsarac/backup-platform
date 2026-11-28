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
  user: { displayName: 'Customer Agent Tenant', role: 'UserAdmin', isPlatformAdmin: false },
  tenants: [{ tenantId: 'one', name: 'Customer Agent Tenant' }, { tenantId: 'two', name: 'English Customer Name' }],
}) }))

describe('Language consumers', () => {
  it('switches complete login copy without remounting or translating entered values, and localizes formatters', () => {
    render(<MemoryRouter><LoginPage /></MemoryRouter>)
    fireEvent.change(screen.getByRole('textbox', { name: /^Kullanıcı adı( \*)?$/ }), { target: { value: 'Customer Agent Tenant' } })
    expect(formatGb(2_500_000_000)).toBe('2,5 GB')
    fireEvent.click(screen.getByRole('button', { name: 'TR / EN: Dili değiştir' }))
    expect(screen.getByRole('heading', { name: 'Sign in' })).toBeInTheDocument()
    expect(screen.getByRole('textbox', { name: /^Username( \*)?$/ })).toHaveValue('Customer Agent Tenant')
    expect(screen.getByLabelText(/^Password( \*)?$/)).toBeInTheDocument()
    expect(localStorage.getItem('bp_language')).toBe('en')
    expect(document.documentElement.lang).toBe('en')
    expect(formatGb(2_500_000_000)).toBe('2.5 GB')
    expect(parseGb(toGbInput(2_500_000_000))).toEqual({ bytes: 2_500_000_000 })
    const date = '2026-10-16T12:15:00Z'
    expect(formatDateTime(date)).toBe(new Intl.DateTimeFormat('en-US', { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(date)))
  })

  it('preserves customer names and restores the saved preference', () => {
    localStorage.setItem('bp_language', 'en')
    render(<MemoryRouter><TenantSelectPage /></MemoryRouter>)
    expect(screen.getByText('Customer Agent Tenant')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'TR / EN: Change language' }))
    expect(screen.getByText('Customer Agent Tenant')).toBeInTheDocument()
    expect(screen.getByText('English Customer Name')).toBeInTheDocument()
    expect(document.documentElement.lang).toBe('tr')
  })
})

describe('English plurals', () => {
  it('uses the singular form only for exactly one', async () => {
    const { changeLanguage, tx } = await import('@/lib/i18n/runtime')
    changeLanguage('en')
    try {
      expect(tx('{0} kopya', { '0': 1 })).toBe('1 copy')
      expect(tx('{0} kopya', { '0': 2 })).toBe('2 copies')
      expect(tx('{0} kayıt', { '0': 0 })).toBe('0 records')
      expect(tx('{0}. deneme', { '0': 1 })).toBe('Attempt 1')
    } finally { changeLanguage('tr') }
  })
})
