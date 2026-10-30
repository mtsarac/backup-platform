import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { Card } from './card'
import { TextInput } from './text-input'
import { Field } from './field'

describe('Card', () => {
  it('renders the chosen element with its attributes and the danger tone', () => {
    render(<Card as="section" aria-label="Tehlikeli bölge" tone="danger" padding="none" className="extra">içerik</Card>)
    const card = screen.getByRole('region', { name: 'Tehlikeli bölge' })
    expect(card).toHaveClass('border-error/40', 'extra')
    expect(card).not.toHaveClass('p-4')
  })
})

describe('TextInput', () => {
  it('takes the Field control props and is a text box by default', () => {
    render(<Field label="Ad" error="Ad gerekli">{control => <TextInput {...control} />}</Field>)
    const input = screen.getByRole('textbox', { name: 'Ad' })
    expect(input).toBeInvalid()
    expect(input).toHaveAccessibleDescription('Ad gerekli')
  })
})
