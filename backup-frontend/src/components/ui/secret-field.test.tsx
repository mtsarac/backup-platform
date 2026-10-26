import { cleanup, fireEvent, screen } from '@testing-library/react'
import { render } from '@/test/render'
import { afterEach, describe, expect, it } from 'vitest'
import { SecretField } from './secret-field'
import { useSecretValue } from './secret-value'

afterEach(cleanup)
const SECRET = 'hunter2-very-secret'

function Form({ saved = false, multiline = false, onSubmit }: { saved?: boolean; multiline?: boolean; onSubmit?: (value: string) => void }) {
  const secret = useSecretValue()
  return <form onSubmit={event => { event.preventDefault(); onSubmit?.(secret.take()) }}>
    <SecretField label="Parola" value={secret.value} onChange={secret.set} saved={saved} multiline={multiline} />
    <button type="submit">Kaydet</button>
  </form>
}

describe('SecretField', () => {
  it('is a password input that never puts the typed value into the page text', () => {
    const { container } = render(<Form />)
    const input = screen.getByLabelText('Parola')
    expect(input).toHaveAttribute('type', 'password')
    fireEvent.change(input, { target: { value: SECRET } })
    expect(container).not.toHaveTextContent(SECRET)
    expect(container.innerHTML).not.toContain(`>${SECRET}<`)
    expect(screen.queryByRole('button', { name: /göster|show/i })).not.toBeInTheDocument()
  })
  it('shows a saved indicator while empty and never the saved value', () => {
    render(<Form saved />)
    expect(screen.getByText('Kayıtlı')).toBeInTheDocument()
    expect(screen.getByLabelText('Parola')).toHaveValue('')
    fireEvent.change(screen.getByLabelText('Parola'), { target: { value: SECRET } })
    expect(screen.queryByText('Kayıtlı')).not.toBeInTheDocument()
  })
  it('has no saved indicator when nothing is stored', () => {
    render(<Form />)
    expect(screen.queryByText('Kayıtlı')).not.toBeInTheDocument()
  })
  it('hands the value over once on submit and clears the field', () => {
    const submitted: string[] = []
    render(<Form onSubmit={value => submitted.push(value)} />)
    fireEvent.change(screen.getByLabelText('Parola'), { target: { value: SECRET } })
    fireEvent.click(screen.getByRole('button', { name: 'Kaydet' }))
    expect(submitted).toEqual([SECRET])
    expect(screen.getByLabelText('Parola')).toHaveValue('')
  })
  it('masks pasted multiline keys, preserves every byte for submission, and supports clearing and replacing them', () => {
    const key = ['test-key-header', 'private-key-material', 'test-key-footer', ''].join('\n')
    const submitted: string[] = []
    const { container } = render(<Form multiline onSubmit={value => submitted.push(value)} />)
    const input = screen.getByLabelText('Parola')
    expect(input).toHaveAttribute('type', 'password')
    fireEvent.paste(input, { clipboardData: { getData: () => key } })
    expect(container.textContent).not.toContain('private-key-material')
    fireEvent.keyDown(input, { key: 'Backspace' })
    expect(input).toHaveValue('')
    fireEvent.paste(input, { clipboardData: { getData: () => key } })
    fireEvent.click(screen.getByRole('button', { name: 'Kaydet' }))
    expect(submitted).toEqual([key])
    expect(input).toHaveValue('')
  })
})
