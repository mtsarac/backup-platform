import { describe, it, expect } from 'vitest'
import { screen, fireEvent } from '@testing-library/react'
import { render } from '@/test/render'
import { tx, useLanguage } from '@/lib/i18n'
import { LanguageSwitcher } from './language-switcher'

function TestConsumer() {
  const { language } = useLanguage()
  return (
    <div>
      <span data-testid="current-lang">{language}</span>
      <span data-testid="translated-title">{tx("Ana Sayfa")}</span>
      <span data-testid="translated-backup">{tx("Yedekleme işleri")}</span>
      <LanguageSwitcher />
    </div>
  )
}

describe('LanguageSwitcher', () => {
  it('switches the whole UI between Turkish and English and remembers the choice', () => {
    render(<TestConsumer />)
    expect(screen.getByTestId('current-lang')).toHaveTextContent('tr')
    expect(screen.getByTestId('translated-title')).toHaveTextContent('Ana Sayfa')
    expect(screen.getByTestId('translated-backup')).toHaveTextContent('Yedekleme işleri')

    fireEvent.click(screen.getByRole('button', { name: /Dili değiştir/ }))
    expect(screen.getByTestId('current-lang')).toHaveTextContent('en')
    expect(screen.getByTestId('translated-title')).toHaveTextContent('Home')
    expect(screen.getByTestId('translated-backup')).toHaveTextContent('Backup jobs')
    expect(localStorage.getItem('bp_language')).toBe('en')
    expect(document.documentElement.lang).toBe('en')

    fireEvent.click(screen.getByRole('button', { name: /Change language/ }))
    expect(screen.getByTestId('current-lang')).toHaveTextContent('tr')
    expect(localStorage.getItem('bp_language')).toBe('tr')
  })

  it('marks the active language by weight and underline, not colour or opacity alone, and keeps the visible text in the accessible name', () => {
    const { container } = render(<LanguageSwitcher />)
    const button = screen.getByRole('button', { name: /Dili değiştir/ })
    expect(button).toHaveAccessibleName(expect.stringContaining('TR / EN'))
    const [tr, en] = [screen.getByText('TR'), screen.getByText('EN')]
    expect(tr).toHaveAttribute('aria-current', 'true')
    expect(tr).toHaveClass('font-bold', 'underline')
    expect(en).not.toHaveAttribute('aria-current')
    expect(en).not.toHaveClass('underline')

    fireEvent.click(button)
    expect(screen.getByTitle('Change language')).toHaveAccessibleName('Change language: TR / EN')
    expect(screen.getByText('EN')).toHaveAttribute('aria-current', 'true')
    expect(screen.getByText('TR')).not.toHaveAttribute('aria-current')
    // opacity would dim the text below 4.5:1 on top of the muted token
    for (const span of container.querySelectorAll('button > span')) expect(span.className).not.toMatch(/opacity/)
    expect(container.querySelector('[aria-hidden="true"]:not(svg)')).toHaveTextContent('/')
  })
})
