import { describe, expect, it } from 'vitest'
import { formatBandwidth, formatExactRate, parseBandwidth, toInput } from './units'

const MIN = 16_000
const MAX = 100_000_000_000

describe('upload bandwidth units (Mbit/s only)', () => {
  it.each([
    ['10', 1_250_000], ['0,128', 16_000], ['1.5', 187_500], [' 8 ', 1_000_000], ['0,128008', 16_001],
  ] as const)('parses %j Mbit/s to %d B/s exactly', (text, bytes) => {
    expect(parseBandwidth(text, MIN, MAX)).toEqual({ bytesPerSecond: bytes })
  })

  it('accepts exactly the limits and rejects beyond them', () => {
    expect(parseBandwidth('0,128', MIN, MAX)).toEqual({ bytesPerSecond: MIN })
    expect(parseBandwidth('0,127', MIN, MAX)).toEqual({ error: 'out_of_range' })
    expect(parseBandwidth('800000', MIN, MAX)).toEqual({ bytesPerSecond: MAX })
    expect(parseBandwidth('800000,000008', MIN, MAX)).toEqual({ error: 'out_of_range' })
    expect(parseBandwidth('999999999999999', MIN, MAX)).toEqual({ error: 'out_of_range' })
  })

  it.each(['', 'abc', '-1', '1e3', '1,', ',5', '1 000', '1,1234567'])('rejects %j as invalid', text => {
    expect(parseBandwidth(text, MIN, MAX)).toEqual({ error: 'invalid' })
  })

  it('shows every value in Mbit/s and keeps the exact byte rate separate', () => {
    expect(formatBandwidth(1_250_000)).toBe('10 Mbit/sn')
    expect(formatBandwidth(16_000)).toBe('0,128 Mbit/sn')
    expect(formatBandwidth(100_000_000_000)).toBe('800.000 Mbit/sn')
    expect(formatExactRate(1_250_000)).toBe('1.250.000 B/sn')
  })

  it('round-trips representative values', () => {
    for (const bytes of [16_000, 16_001, 125_000, 1_250_000, 1_500_000, 123_456_789, MAX]) {
      expect(parseBandwidth(toInput(bytes), MIN, MAX)).toEqual({ bytesPerSecond: bytes })
    }
  })
})
