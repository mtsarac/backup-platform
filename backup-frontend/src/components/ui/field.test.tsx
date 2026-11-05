import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { Field } from './field'

describe('Field', () => {
  it('names the control and wires hint and error as its description', () => {
    render(<Field label="Ad" hint="En fazla 100 karakter" error="Ad gerekli">{control => <input {...control} />}</Field>)
    const input = screen.getByLabelText('Ad')
    expect(input).toHaveAccessibleDescription('En fazla 100 karakter Ad gerekli')
    expect(input).toBeInvalid()
    expect(screen.getByRole('alert')).toHaveTextContent('Ad gerekli')
  })

  it('leaves the control valid and undescribed without hint or error, and hides the required star from the accessible name', () => {
    render(<Field label="Ad" required>{control => <input {...control} />}</Field>)
    const input = screen.getByRole('textbox', { name: 'Ad' })
    expect(input).not.toHaveAttribute('aria-describedby')
    expect(input).not.toHaveAttribute('aria-invalid')
    expect(screen.queryByRole('alert')).toBeNull()
  })
})
