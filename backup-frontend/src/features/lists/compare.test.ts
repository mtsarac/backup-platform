import { expect, it } from 'vitest'
import { compareDate, compareNumber, compareText } from './compare'

it('orders values ascending', () => {
  expect(compareText('a', 'b')).toBeLessThan(0)
  expect(compareNumber(2, 1)).toBeGreaterThan(0)
  expect(compareDate('2026-01-02T00:00:00Z', '2026-01-01T00:00:00Z')).toBeGreaterThan(0)
  expect(compareNumber(1, 1)).toBe(0)
})

it('puts a missing value last whichever side it is on', () => {
  for (const missing of [null, undefined, '']) {
    expect(compareDate(missing, '2026-01-01')).toBe(Infinity)
    expect(compareDate('2026-01-01', missing)).toBe(-Infinity)
  }
  expect(compareText(null, undefined)).toBe(0)
  expect(compareNumber(NaN, 3)).toBe(Infinity)
  expect(compareNumber(0, null)).toBe(-Infinity)
})
