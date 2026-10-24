import { describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { TechnicalDetails } from './technical-details'

const items = [{ label: 'Hata kodu', value: 'quota_exceeded' }, { label: 'Kimlik', value: 'a1b2c3' }]

describe('TechnicalDetails', () => {
  it('is closed by default and shows its values once opened', async () => {
    render(<TechnicalDetails items={items} />)
    expect(screen.getByText('Teknik ayrıntılar')).toBeVisible()
    expect(screen.getByText('quota_exceeded')).not.toBeVisible()
    await userEvent.click(screen.getByText('Teknik ayrıntılar'))
    expect(screen.getByText('quota_exceeded')).toBeVisible()
    expect(screen.getByText('a1b2c3')).toBeVisible()
  })

  it('lets every value be copied', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined)
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true })
    render(<TechnicalDetails items={items} defaultOpen />)
    await userEvent.click(screen.getByRole('button', { name: 'Kopyala: Hata kodu' }))
    expect(writeText).toHaveBeenCalledWith('quota_exceeded')
    await userEvent.click(screen.getByRole('button', { name: 'Kopyala: Kimlik' }))
    expect(writeText).toHaveBeenCalledWith('a1b2c3')
  })

  it('renders nothing when there is nothing technical to show', () => {
    const { container } = render(<TechnicalDetails items={[]} />)
    expect(container).toBeEmptyDOMElement()
  })
})
