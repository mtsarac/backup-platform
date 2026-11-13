import { describe, expect, it, vi } from 'vitest'
import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { TechnicalDetails } from './technical-details'

const items = [{ label: 'Hata kodu', value: 'quota_exceeded' }, { label: 'Kimlik', value: 'a1b2c3' }]

describe('TechnicalDetails', () => {
  it('is closed by default and opens a centered dialog, not a drawer, with Escape and focus restore', async () => {
    render(<TechnicalDetails items={items} />)
    const trigger = screen.getByRole('button', { name: 'Teknik ayrıntılar' })
    expect(screen.queryByRole('dialog')).toBeNull()
    await userEvent.click(trigger)
    const dialog = await screen.findByRole('dialog', { name: 'Teknik ayrıntılar' })
    expect(dialog.className).not.toMatch(/slide-in|right-0/)
    expect(within(dialog).getByText('quota_exceeded')).toBeVisible()
    expect(within(dialog).getByText('a1b2c3')).toBeVisible()
    await userEvent.keyboard('{Escape}')
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    expect(trigger).toHaveFocus()
  })

  it('copies everything at once and filters long lists by search', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined)
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true })
    const many = Array.from({ length: 6 }, (_, n) => ({ label: `Alan ${n}`, value: `değer-${n}` }))
    render(<TechnicalDetails items={many} defaultOpen />)
    await userEvent.click(await screen.findByRole('button', { name: 'Tümünü kopyala' }))
    expect(JSON.parse(writeText.mock.calls[0][0])).toEqual(Object.fromEntries(many.map(i => [i.label, i.value])))
    await userEvent.type(screen.getByRole('searchbox', { name: 'Ara' }), 'değer-3')
    expect(screen.getByText('değer-3')).toBeVisible()
    expect(screen.queryByText('değer-2')).toBeNull()
  })

  it('never opens a second modal inside a dialog: it falls back to the accordion', () => {
    render(<div role="dialog"><TechnicalDetails items={items} /></div>)
    expect(screen.getAllByRole('dialog')).toHaveLength(1)
    expect(screen.getByText('Teknik ayrıntılar').tagName).toBe('SUMMARY')
  })

  it('lets every value be copied', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined)
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true })
    render(<TechnicalDetails items={items} defaultOpen />)
    await userEvent.click(await screen.findByRole('button', { name: 'Kopyala: Hata kodu' }))
    expect(writeText).toHaveBeenCalledWith('quota_exceeded')
    await userEvent.click(screen.getByRole('button', { name: 'Kopyala: Kimlik' }))
    expect(writeText).toHaveBeenCalledWith('a1b2c3')
  })

  it('can hold richer technical content that stays hidden until opened', async () => {
    render(<TechnicalDetails items={[]}><p>Kopya tablosu</p></TechnicalDetails>)
    expect(screen.queryByText('Kopya tablosu')).toBeNull()
    await userEvent.click(screen.getByRole('button', { name: 'Teknik ayrıntılar' }))
    expect(await screen.findByText('Kopya tablosu')).toBeVisible()
  })

  it.each([['accordion', true], ['modal', false]])('keeps its %s a valid description list: only term/definition groups, copy button inside a dd', async (_mode, accordion) => {
    render(<TechnicalDetails items={items} defaultOpen accordion={accordion}><p>Ek içerik</p></TechnicalDetails>)
    await screen.findByText('Ek içerik')
    const list = document.querySelector('dl')!
    expect(list.children).toHaveLength(items.length)
    for (const group of list.children) {
      expect(group.tagName).toBe('DIV')
      expect([...group.children].map(child => child.tagName)).toEqual(['DT', 'DD', 'DD'])
      expect(group.lastElementChild).toContainElement(within(group as HTMLElement).getByRole('button'))
    }
    expect(list).not.toContainElement(screen.getByText('Ek içerik'))
  })

  it('renders nothing when there is nothing technical to show', () => {
    const { container } = render(<TechnicalDetails items={[]} />)
    expect(container.querySelector('button, details')).toBeNull()
  })
})
