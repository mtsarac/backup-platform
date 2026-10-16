import { describe, it, expect } from 'vitest'
import { screen, fireEvent } from '@testing-library/react'
import { render } from '@/test/render'
import { useTranslation } from '@/lib/i18n'
import { LanguageSwitcher } from './language-switcher'

function TestConsumer() {
  const { t, language } = useTranslation()
  return (
    <div>
      <span data-testid="current-lang">{language}</span>
      <span data-testid="translated-title">{t('nav.dashboard')}</span>
      <span data-testid="translated-backup">{t('nav.backupJobs')}</span>
      <LanguageSwitcher />
    </div>
  )
}

describe('LanguageSwitcher', () => {
  it('switches the whole UI between Turkish and English and remembers the choice', () => {
    render(<TestConsumer />)
    expect(screen.getByTestId('current-lang')).toHaveTextContent('tr')
    expect(screen.getByTestId('translated-title')).toHaveTextContent('Gösterge Paneli')
    expect(screen.getByTestId('translated-backup')).toHaveTextContent('Yedekleme İşleri')

    fireEvent.click(screen.getByRole('button', { name: /Dili değiştir/ }))
    expect(screen.getByTestId('current-lang')).toHaveTextContent('en')
    expect(screen.getByTestId('translated-title')).toHaveTextContent('Dashboard')
    expect(screen.getByTestId('translated-backup')).toHaveTextContent('Backup Jobs')
    expect(localStorage.getItem('bp_language')).toBe('en')
    expect(document.documentElement.lang).toBe('en')

    fireEvent.click(screen.getByRole('button', { name: /Change language/ }))
    expect(screen.getByTestId('current-lang')).toHaveTextContent('tr')
    expect(localStorage.getItem('bp_language')).toBe('tr')
  })
})
