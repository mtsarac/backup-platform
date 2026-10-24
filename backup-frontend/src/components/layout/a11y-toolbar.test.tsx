import { act, screen } from '@testing-library/react'
import { render } from '@/test/render'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ShellBar } from './top-bar'
import { ThemeProvider } from '@/components/theme-provider'
import { applyA11yPrefs } from '@/lib/a11y-prefs'
import { changeLanguage } from '@/lib/i18n/runtime'

vi.mock('@/features/auth/use-auth', () => ({ useAuth: () => ({ user: { displayName: 'Ada' }, logout: vi.fn() }) }))

function renderDisplayMenu() {
  return render(<ThemeProvider><ShellBar onMenuToggle={() => {}} context="Workspace" /></ThemeProvider>)
}

async function openDisplayMenu() {
  const user = userEvent.setup()
  await user.click(screen.getByRole('button', { name: 'Görünüm' }))
  return user
}

describe('Shell display menu', () => {
  beforeEach(() => {
    localStorage.clear()
    localStorage.setItem('backup-theme', 'light')
    document.documentElement.removeAttribute('style')
    document.documentElement.classList.remove('hc')
  })

  it('keeps display preferences behind one keyboard-operable button and restores focus on Escape', async () => {
    renderDisplayMenu()
    const trigger = screen.getByRole('button', { name: 'Görünüm' })
    expect(screen.queryByRole('button', { name: 'Yazıyı büyüt' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Yüksek kontrast' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Temayı değiştir' })).not.toBeInTheDocument()
    const user = userEvent.setup()
    await user.tab()
    await user.tab()
    expect(trigger).toHaveFocus()
    await user.keyboard('{Enter}')
    expect(trigger).toHaveAttribute('aria-expanded', 'true')
    const increase = screen.getByRole('button', { name: 'Yazıyı büyüt' })
    expect(increase).toBeVisible()
    expect(increase).toHaveFocus()
    await user.keyboard(' ')
    expect(document.documentElement.style.fontSize).toBe('125%')
    await user.tab()
    expect(screen.getByRole('button', { name: 'Yazıyı küçült' })).toHaveFocus()
    await user.tab()
    expect(screen.getByRole('button', { name: 'Yüksek kontrast' })).toHaveFocus()
    await user.keyboard(' ')
    expect(document.documentElement).toHaveClass('hc')
    await user.tab()
    expect(screen.getByRole('button', { name: 'Temayı değiştir' })).toHaveFocus()
    await user.keyboard(' ')
    expect(document.documentElement).toHaveClass('dark')
    await user.keyboard('{Escape}')
    expect(trigger).toHaveFocus()
    expect(trigger).toHaveAttribute('aria-expanded', 'false')
    expect(screen.queryByRole('button', { name: 'Yazıyı büyüt' })).not.toBeInTheDocument()
  })

  it('restores persisted preferences at startup and reflects them in the controls', async () => {
    localStorage.setItem('backup-font-scale', '125')
    localStorage.setItem('backup-high-contrast', '1')
    localStorage.setItem('backup-theme', 'dark')
    applyA11yPrefs()
    renderDisplayMenu()
    await openDisplayMenu()
    expect(document.documentElement.style.fontSize).toBe('125%')
    expect(document.documentElement).toHaveClass('hc')
    expect(document.documentElement).toHaveClass('dark')
    expect(screen.getByText('%125')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Yüksek kontrast' })).toHaveAttribute('aria-pressed', 'true')
    expect(screen.getByRole('button', { name: 'Temayı değiştir' })).toHaveAttribute('aria-pressed', 'true')
  })

  it('ignores an unsupported persisted scale', async () => {
    localStorage.setItem('backup-font-scale', '999')
    applyA11yPrefs()
    renderDisplayMenu()
    await openDisplayMenu()
    expect(document.documentElement.style.fontSize).toBe('100%')
    expect(screen.getByRole('button', { name: 'Yazıyı küçült' })).toBeDisabled()
  })

  it('scales the root font size within 100-150% and persists it', async () => {
    renderDisplayMenu()
    const user = await openDisplayMenu()
    expect(screen.getByRole('button', { name: 'Yazıyı küçült' })).toBeDisabled()
    await user.click(screen.getByRole('button', { name: 'Yazıyı büyüt' }))
    await user.click(screen.getByRole('button', { name: 'Yazıyı büyüt' }))
    expect(document.documentElement.style.fontSize).toBe('150%')
    expect(screen.getByRole('button', { name: 'Yazıyı büyüt' })).toBeDisabled()
    expect(localStorage.getItem('backup-font-scale')).toBe('150')
    await user.click(screen.getByRole('button', { name: 'Yazıyı küçült' }))
    await user.click(screen.getByRole('button', { name: 'Yazıyı küçült' }))
    expect(document.documentElement.style.fontSize).toBe('100%')
    expect(screen.getByRole('button', { name: 'Yazıyı küçült' })).toBeDisabled()
    expect(localStorage.getItem('backup-font-scale')).toBe('100')
  })

  it('toggles high contrast', async () => {
    renderDisplayMenu()
    const user = await openDisplayMenu()
    await user.click(screen.getByRole('button', { name: 'Yüksek kontrast' }))
    expect(document.documentElement).toHaveClass('hc')
    expect(screen.getByRole('button', { name: 'Yüksek kontrast' })).toHaveAttribute('aria-pressed', 'true')
    expect(localStorage.getItem('backup-high-contrast')).toBe('1')
    await user.click(screen.getByRole('button', { name: 'Yüksek kontrast' }))
    expect(document.documentElement).not.toHaveClass('hc')
    expect(localStorage.getItem('backup-high-contrast')).toBe('0')
  })

  it('changes the persisted theme from inside the display menu', async () => {
    localStorage.setItem('backup-theme', 'light')
    renderDisplayMenu()
    const user = await openDisplayMenu()
    await user.click(screen.getByRole('button', { name: 'Temayı değiştir' }))
    expect(document.documentElement).toHaveClass('dark')
    expect(localStorage.getItem('backup-theme')).toBe('dark')
  })

  it('offers the same display controls in English', async () => {
    renderDisplayMenu()
    act(() => changeLanguage('en'))
    await userEvent.setup().click(screen.getByRole('button', { name: 'Display' }))
    expect(screen.getByRole('button', { name: 'Increase text size' })).toBeVisible()
    expect(screen.getByRole('button', { name: 'High contrast' })).toBeVisible()
    expect(screen.getByRole('button', { name: 'Toggle theme' })).toBeVisible()
  })
})
