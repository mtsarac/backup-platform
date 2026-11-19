import userEvent from '@testing-library/user-event'
import { screen } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ThemeProvider } from '@/components/theme-provider'
import { renderPlatform } from '@/features/platform/test-utils'
import { TopBar } from './top-bar'

vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => ({ tenantId: 'tenant-a', tenant: { tenantId: 'tenant-a', name: 'Acme' }, switchTenant: vi.fn() }) }))
vi.mock('@/features/notifications/in-app', () => ({ InAppNotifications: () => <button type="button">Bildirimler</button> }))

const renderBar = () => renderPlatform(<ThemeProvider><TopBar onMenuToggle={vi.fn()} /></ThemeProvider>, { tenants: [{ tenantId: 'tenant-a', name: 'Acme' } as never] })

describe('Top bar', () => {
  beforeEach(() => {
    localStorage.clear()
    document.documentElement.removeAttribute('style')
    document.documentElement.className = ''
  })

  it('switches and persists the theme from the keyboard, offering nothing else', async () => {
    localStorage.setItem('backup-theme', 'light')
    renderBar()
    const toggle = screen.getByRole('button', { name: 'Temayı değiştir' })
    toggle.focus()
    await userEvent.setup().keyboard('{Enter}')
    expect(document.documentElement).toHaveClass('dark')
    expect(localStorage.getItem('backup-theme')).toBe('dark')
    expect(toggle).toHaveAttribute('aria-pressed', 'true')
  })

  it('opens with the normal theme and text size when a high-contrast or large-text preference was saved earlier', () => {
    localStorage.setItem('backup-font-scale', '150')
    localStorage.setItem('backup-high-contrast', '1')
    renderBar()
    expect(document.documentElement).not.toHaveClass('hc')
    expect(document.documentElement).not.toHaveClass('dark')
    expect(document.documentElement.style.fontSize).toBe('')
    expect(screen.getByRole('button', { name: 'Temayı değiştir' })).toHaveAttribute('aria-pressed', 'false')
  })

  it('keeps workspace, notification, language and theme controls but no account links, name or sign-out', () => {
    renderPlatform(<ThemeProvider><TopBar onMenuToggle={vi.fn()} /></ThemeProvider>, { tenants: [{ tenantId: 'tenant-a', name: 'Acme' } as never] })
    expect(screen.getByLabelText('Çalışma Alanı')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Bildirimler' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Temayı değiştir' })).toBeInTheDocument()
    expect(screen.getByText('Platform Yöneticisi')).toBeInTheDocument()
    for (const name of ['Oturumlarım', 'Güvenlik', 'Geçiş anahtarları']) expect(screen.queryByRole('link', { name })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Çıkış' })).not.toBeInTheDocument()
    expect(screen.queryByText('Platform Operatörü')).not.toBeInTheDocument()
  })
})
