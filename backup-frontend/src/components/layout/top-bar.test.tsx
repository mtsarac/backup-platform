import { screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { ThemeProvider } from '@/components/theme-provider'
import { renderPlatform } from '@/features/platform/test-utils'
import { TopBar } from './top-bar'

vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => ({ tenantId: 'tenant-a', tenant: { tenantId: 'tenant-a', name: 'Acme' }, switchTenant: vi.fn() }) }))
vi.mock('@/features/notifications/in-app', () => ({ InAppNotifications: () => <button type="button">Bildirimler</button> }))

describe('Top bar', () => {
  it('keeps workspace, notification, language and display controls but no account links, name or sign-out', () => {
    renderPlatform(<ThemeProvider><TopBar onMenuToggle={vi.fn()} /></ThemeProvider>, { tenants: [{ tenantId: 'tenant-a', name: 'Acme' } as never] })
    expect(screen.getByLabelText('Çalışma Alanı')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Bildirimler' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Görünüm' })).toBeInTheDocument()
    expect(screen.getByText('Platform Yöneticisi')).toBeInTheDocument()
    for (const name of ['Oturumlarım', 'Güvenlik', 'Geçiş anahtarları']) expect(screen.queryByRole('link', { name })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Çıkış' })).not.toBeInTheDocument()
    expect(screen.queryByText('Platform Operatörü')).not.toBeInTheDocument()
  })
})
