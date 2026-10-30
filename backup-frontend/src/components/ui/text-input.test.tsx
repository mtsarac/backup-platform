import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { Select, TextArea, TextInput } from './text-input'

describe('text controls', () => {
  it('render the native control with the shared look and pass props through', () => {
    render(<><TextInput aria-label="a" /><TextArea aria-label="b" /><Select aria-label="c"><option>x</option></Select></>)
    expect(screen.getByRole('textbox', { name: 'a' })).toHaveClass('min-h-11', 'w-full')
    expect(screen.getByRole('textbox', { name: 'b' }).tagName).toBe('TEXTAREA')
    expect(screen.getByRole('combobox', { name: 'c' })).toHaveClass('min-h-11')
  })
})
