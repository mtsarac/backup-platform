import { screen } from '@testing-library/react'
import { render } from '@/test/render'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it } from 'vitest'
import { A11yToolbar } from './a11y-toolbar'
import { applyA11yPrefs } from '@/lib/a11y-prefs'

describe('A11yToolbar', () => {
  beforeEach(() => {
    localStorage.clear()
    document.documentElement.removeAttribute('style')
    document.documentElement.classList.remove('hc')
  })

  it('restores persisted preferences at startup and reflects them in the controls', () => {
    localStorage.setItem('backup-font-scale', '125')
    localStorage.setItem('backup-high-contrast', '1')
    applyA11yPrefs()
    render(<A11yToolbar />)
    expect(document.documentElement.style.fontSize).toBe('125%')
    expect(document.documentElement).toHaveClass('hc')
    expect(screen.getByText('%125')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Yüksek kontrast' })).toHaveAttribute('aria-pressed', 'true')
  })

  it('ignores an unsupported persisted scale', () => {
    localStorage.setItem('backup-font-scale', '999')
    applyA11yPrefs()
    render(<A11yToolbar />)
    expect(document.documentElement.style.fontSize).toBe('100%')
    expect(screen.getByRole('button', { name: 'Yazıyı küçült' })).toBeDisabled()
  })

  it('scales the root font size within 100-150% and persists it', async () => {
    render(<A11yToolbar />)
    const user = userEvent.setup()
    expect(screen.getByRole('button', { name: 'Yazıyı küçült' })).toBeDisabled()
    await user.click(screen.getByRole('button', { name: 'Yazıyı büyüt' }))
    await user.click(screen.getByRole('button', { name: 'Yazıyı büyüt' }))
    expect(document.documentElement.style.fontSize).toBe('150%')
    expect(screen.getByRole('button', { name: 'Yazıyı büyüt' })).toBeDisabled()
    expect(localStorage.getItem('backup-font-scale')).toBe('150')
  })

  it('toggles high contrast', async () => {
    render(<A11yToolbar />)
    await userEvent.setup().click(screen.getByRole('button', { name: 'Yüksek kontrast' }))
    expect(document.documentElement).toHaveClass('hc')
    expect(screen.getByRole('button', { name: 'Yüksek kontrast' })).toHaveAttribute('aria-pressed', 'true')
  })
})
