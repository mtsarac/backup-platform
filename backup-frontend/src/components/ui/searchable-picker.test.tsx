import { useState } from 'react'
import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { SearchablePicker, type PickerOption } from './searchable-picker'

const options: PickerOption[] = [
  { value: 'a', label: 'Muhasebe', description: 'Gece yedeği' },
  { value: 'b', label: 'Satış', description: 'Haftalık' },
  { value: 'c', label: 'İstanbul şubesi' },
]

function Harness({ initial = '', items = options }: { initial?: string; items?: PickerOption[] }) {
  const [value, setValue] = useState(initial)
  return <>
    <SearchablePicker label="Yedek dosyası" placeholder="Tüm yedekler" options={items} value={value} onChange={setValue} />
    <output aria-label="seçili">{value}</output>
  </>
}
const shown = () => screen.queryAllByRole('option').map((option) => option.textContent)

describe('SearchablePicker', () => {
  it('lists every choice when opened and narrows them as the user types a name', async () => {
    render(<Harness />)
    await userEvent.click(screen.getByRole('combobox', { name: 'Yedek dosyası' }))
    expect(shown()).toHaveLength(3)
    await userEvent.keyboard('mu')
    expect(shown()).toEqual(['MuhasebeGece yedeği'])
  })

  it('finds a name regardless of Turkish letter case', async () => {
    render(<Harness />)
    await userEvent.type(screen.getByRole('combobox', { name: 'Yedek dosyası' }), 'istanbul')
    expect(shown()).toEqual(['İstanbul şubesi'])
  })

  it('also searches the secondary line', async () => {
    render(<Harness />)
    await userEvent.type(screen.getByRole('combobox', { name: 'Yedek dosyası' }), 'haftalık')
    expect(shown()).toEqual(['SatışHaftalık'])
  })

  it('picks a choice with the mouse and shows its name in the box', async () => {
    render(<Harness />)
    await userEvent.click(screen.getByRole('combobox', { name: 'Yedek dosyası' }))
    await userEvent.click(screen.getByRole('option', { name: /Satış/ }))
    expect(screen.getByLabelText('seçili')).toHaveTextContent('b')
    expect(screen.getByRole('combobox', { name: 'Yedek dosyası' })).toHaveValue('Satış')
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument()
  })

  it('works from the keyboard: arrows move, Enter picks, Escape closes', async () => {
    render(<Harness />)
    screen.getByRole('combobox', { name: 'Yedek dosyası' }).focus()
    await userEvent.keyboard('{ArrowDown}{ArrowDown}{Enter}')
    expect(screen.getByLabelText('seçili')).toHaveTextContent('b')
    await userEvent.keyboard('{ArrowDown}')
    expect(screen.getByRole('listbox')).toBeInTheDocument()
    await userEvent.keyboard('{Escape}')
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument()
  })

  it('says so when nothing matches and picks nothing on Enter', async () => {
    render(<Harness />)
    await userEvent.type(screen.getByRole('combobox', { name: 'Yedek dosyası' }), 'zzz{Enter}')
    expect(screen.getByText('Eşleşen sonuç yok')).toBeInTheDocument()
    expect(screen.getByLabelText('seçili')).toHaveTextContent('')
  })

  it('shows the current choice and lets the user clear it', async () => {
    render(<Harness initial="a" />)
    expect(screen.getByRole('combobox', { name: 'Yedek dosyası' })).toHaveValue('Muhasebe')
    await userEvent.click(screen.getByRole('button', { name: 'Seçimi temizle' }))
    expect(screen.getByLabelText('seçili')).toHaveTextContent('')
    expect(screen.getByRole('combobox', { name: 'Yedek dosyası' })).toHaveValue('')
  })

  it('offers no clear button when nothing is chosen', () => {
    render(<Harness />)
    expect(screen.queryByRole('button', { name: 'Seçimi temizle' })).not.toBeInTheDocument()
  })

  it('names a current choice that is not among the loaded options', () => {
    render(<SearchablePicker label="Yedek dosyası" options={options} value="old" selectedLabel="Eski yedek" onChange={() => undefined} />)
    expect(screen.getByRole('combobox', { name: 'Yedek dosyası' })).toHaveValue('Eski yedek')
  })

  it('stops offering choices when access is disabled while the list is open', async () => {
    const props = { label: 'Yedek dosyası', options, value: '', onChange: () => undefined }
    const { rerender } = render(<SearchablePicker {...props} />)
    await userEvent.click(screen.getByRole('combobox'))
    expect(shown()).toHaveLength(3)
    rerender(<SearchablePicker {...props} disabled />)
    expect(screen.getByRole('combobox')).toBeDisabled()
    expect(screen.getByRole('combobox')).toHaveAttribute('aria-expanded', 'false')
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument()
  })
})
