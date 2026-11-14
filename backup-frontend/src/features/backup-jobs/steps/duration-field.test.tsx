import { fireEvent, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { render } from '@/test/render'
import { DurationField, durationUnit } from './duration-field'

describe('duration fields', () => {
  it.each([[0, 1], [61, 1], [90, 1], [120, 60], [3600, 3600], [86400, 86400], [1.5, 1]])('chooses an exact unit for %s seconds', (seconds, unit) => {
    expect(durationUnit(seconds)).toBe(unit)
  })

  it('changes units without changing unusual values and converts edits to seconds', () => {
    const onChange = vi.fn()
    render(<DurationField name="duration" label="Duration" value={90} onChange={onChange} />)
    const input = screen.getByLabelText('Duration')
    fireEvent.change(screen.getByRole('combobox'), { target: { value: '60' } })
    expect(onChange).not.toHaveBeenCalled()
    expect(input).toHaveValue(1.5)
    fireEvent.change(input, { target: { value: '2.5' } })
    expect(onChange).toHaveBeenCalledWith(150)
  })

  it('disables both controls while retaining the exact value and error', () => {
    render(<DurationField name="duration" label="Duration" value={61} disabled error="Invalid duration" onChange={() => {}} />)
    expect(screen.getByLabelText('Duration')).toBeDisabled()
    expect(screen.getByLabelText('Duration')).toHaveValue(61)
    expect(screen.getByRole('combobox')).toBeDisabled()
    expect(screen.getByRole('alert')).toHaveTextContent('Invalid duration')
  })
})
