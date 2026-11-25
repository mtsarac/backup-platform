import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { Fact, Facts } from './section'

describe('Fact', () => {
  it('keeps the hint inside the dd so the dl only holds dt and dd children', () => {
    render(<Facts><Fact label="Durum" hint="Son deneme">Başarılı</Fact></Facts>)
    const hint = screen.getByText('Son deneme')
    expect(hint.parentElement?.tagName).toBe('DD')
    expect(hint.closest('div')?.children).toHaveLength(2)
  })
})
