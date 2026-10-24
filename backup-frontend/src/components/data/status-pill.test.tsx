import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import { changeLanguage } from '@/lib/i18n/runtime'
import { StatusPill } from './status-pill'

describe('StatusPill', () => {
  it('shows the three levels as colour, icon and text', () => {
    render(<><StatusPill level="safe" /><StatusPill level="attention" /><StatusPill level="urgent" /></>)
    for (const [text, colour] of [['Güvende', 'text-success'], ['Dikkat', 'text-warning'], ['Acil', 'text-error']] as const) {
      const pill = screen.getByText(text).closest('span')!
      expect(pill).toHaveClass(colour)
      expect(pill.querySelector('svg')).not.toBeNull()
    }
  })

  it('gives each level a different icon so colour is never the only cue', () => {
    render(<><StatusPill level="safe" /><StatusPill level="attention" /><StatusPill level="urgent" /><StatusPill level="running" /><StatusPill level="pending" /></>)
    const icons = ['Güvende', 'Dikkat', 'Acil', 'Çalışıyor', 'Beklemede'].map(text => screen.getByText(text).closest('span')!.querySelector('svg')!.getAttribute('class'))
    expect(new Set(icons).size).toBe(icons.length)
  })

  it('looks up a status in the one dictionary', () => {
    render(<><StatusPill status="Succeeded" /><StatusPill status="Failed" /><StatusPill status="Running" /><StatusPill status="Pending" /></>)
    expect(screen.getByText('Başarılı').closest('span')).toHaveClass('text-success')
    expect(screen.getByText('Başarısız').closest('span')).toHaveClass('text-error')
    expect(screen.getByText('Çalışıyor').closest('span')).toHaveClass('text-info')
    expect(screen.getByText('Sırada bekliyor').closest('span')).toHaveClass('text-text-muted')
  })

  it('keeps the same status word distinct per domain', () => {
    render(<><StatusPill domain="subscription" status="Expired" /><StatusPill domain="breakGlass" status="Expired" /></>)
    const [subscription, breakGlass] = screen.getAllByText('Süresi doldu').map(node => node.closest('span')!)
    expect(subscription).toHaveClass('text-error')
    expect(breakGlass).not.toHaveClass('text-error')
  })

  it('shows an unknown status as neutral text, never hiding it', () => {
    render(<StatusPill status="SomethingNew" />)
    expect(screen.getByText('SomethingNew').closest('span')).toHaveClass('text-text-muted')
  })

  it('uses a custom label for a computed state', () => {
    render(<StatusPill level="attention">Test edilmedi</StatusPill>)
    expect(screen.getByText('Test edilmedi').closest('span')).toHaveClass('text-warning')
  })

  it('switches to English with the interface language', () => {
    changeLanguage('en')
    render(<StatusPill level="safe" />)
    expect(screen.getByText('Safe')).toBeInTheDocument()
  })
})
