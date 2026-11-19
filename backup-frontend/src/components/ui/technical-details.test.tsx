import { describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { TechnicalDetails } from './technical-details'

const items = [{ label: 'Hata kodu', value: 'quota_exceeded' }, { label: 'Kimlik', value: 'a1b2c3' }]

describe('TechnicalDetails', () => {
  it('is one collapsed disclosure that opens inline, not a dialog', async () => {
    render(<TechnicalDetails items={items} />)
    const trigger = screen.getByRole('button', { name: 'Teknik ayrıntılar' })
    expect(trigger).toHaveAttribute('aria-expanded', 'false')
    expect(screen.queryByText('quota_exceeded')).toBeNull()
    await userEvent.click(trigger)
    expect(trigger).toHaveAttribute('aria-expanded', 'true')
    expect(screen.queryByRole('dialog')).toBeNull()
    expect(screen.getByRole('region', { name: 'Teknik ayrıntılar' })).toHaveTextContent('Hata kodu: quota_exceeded')
    expect(screen.getByText('a1b2c3')).toBeVisible()
    await userEvent.click(trigger)
    expect(screen.queryByText('quota_exceeded')).toBeNull()
  })

  it('has one Kopyala button that copies every line as `Anahtar: değer`', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined)
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true })
    render(<TechnicalDetails items={items} defaultOpen />)
    await userEvent.click(screen.getByRole('button', { name: 'Kopyala' }))
    expect(writeText).toHaveBeenCalledWith('Hata kodu: quota_exceeded\nKimlik: a1b2c3')
    expect(screen.getAllByRole('button', { name: /Kopyala/ })).toHaveLength(1)
  })

  it('can hold richer technical content that stays hidden until opened', async () => {
    render(<TechnicalDetails items={[]}><p>Kopya tablosu</p></TechnicalDetails>)
    expect(screen.queryByText('Kopya tablosu')).toBeNull()
    await userEvent.click(screen.getByRole('button', { name: 'Teknik ayrıntılar' }))
    expect(screen.getByText('Kopya tablosu')).toBeVisible()
    expect(screen.queryByRole('button', { name: 'Kopyala' })).toBeNull()
  })

  it('renders nothing when there is nothing technical to show', () => {
    const { container } = render(<TechnicalDetails items={[]} />)
    expect(container).toBeEmptyDOMElement()
  })
})
