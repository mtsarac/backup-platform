import { screen } from '@testing-library/react'
import { render } from '@/test/render'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it } from 'vitest'
import { ThemeToggle } from './theme-toggle'
import { ThemeProvider } from '@/components/theme-provider'

const renderToggle = () => render(<ThemeProvider><ThemeToggle /></ThemeProvider>)

describe('Theme toggle', () => {
  beforeEach(() => {
    localStorage.clear()
    document.documentElement.removeAttribute('style')
    document.documentElement.className = ''
  })

  it('switches and persists the theme from the keyboard, offering nothing else', async () => {
    localStorage.setItem('backup-theme', 'light')
    renderToggle()
    const toggle = screen.getByRole('button', { name: 'Temayı değiştir' })
    expect(screen.getAllByRole('button')).toHaveLength(1)
    await userEvent.setup().tab()
    expect(toggle).toHaveFocus()
    await userEvent.setup().keyboard('{Enter}')
    expect(document.documentElement).toHaveClass('dark')
    expect(localStorage.getItem('backup-theme')).toBe('dark')
    expect(toggle).toHaveAttribute('aria-pressed', 'true')
  })

  it('opens with the normal theme and text size when a high-contrast or large-text preference was saved earlier', () => {
    localStorage.setItem('backup-font-scale', '150')
    localStorage.setItem('backup-high-contrast', '1')
    renderToggle()
    expect(document.documentElement).not.toHaveClass('hc')
    expect(document.documentElement).not.toHaveClass('dark')
    expect(document.documentElement.style.fontSize).toBe('')
    expect(screen.getByRole('button', { name: 'Temayı değiştir' })).toHaveAttribute('aria-pressed', 'false')
  })
})
