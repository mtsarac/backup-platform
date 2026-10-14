import { describe, expect, it } from 'vitest'
import { bandwidthUnits, formatBandwidth, formatExactRate, parseBandwidth, toInput } from './units'

const MIN = 16_000
const MAX = 100_000_000_000

describe('upload bandwidth units (bits and bytes are never mixed)', () => {
  it('defines Mbit/s as 125,000 B/s and MB/s as 1,000,000 B/s', () => {
    expect(Object.fromEntries(bandwidthUnits.map(unit => [unit.id, unit.bytesPerSecond]))).toEqual({ 'Mbit/s': 125_000, 'MB/s': 1_000_000, 'KB/s': 1_000, 'B/s': 1 })
  })

  it.each([
    ['10', 'Mbit/s', 1_250_000], ['10', 'MB/s', 10_000_000], ['128', 'KB/s', 128_000], ['0,128', 'Mbit/s', 16_000],
    ['1.5', 'MB/s', 1_500_000], [' 8 ', 'Mbit/s', 1_000_000], ['16000', 'B/s', 16_000], ['0,016', 'MB/s', 16_000],
  ] as const)('parses %j %s to %d B/s exactly', (text, unit, bytes) => {
    expect(parseBandwidth(text, unit, MIN, MAX)).toEqual({ bytesPerSecond: bytes })
  })

  it('accepts exactly the limits and rejects one byte beyond them', () => {
    expect(parseBandwidth('16000', 'B/s', MIN, MAX)).toEqual({ bytesPerSecond: MIN })
    expect(parseBandwidth('15999', 'B/s', MIN, MAX)).toEqual({ error: 'out_of_range' })
    expect(parseBandwidth('100000', 'MB/s', MIN, MAX)).toEqual({ bytesPerSecond: MAX })
    expect(parseBandwidth('100000000001', 'B/s', MIN, MAX)).toEqual({ error: 'out_of_range' })
    expect(parseBandwidth('800000,000008', 'Mbit/s', MIN, MAX)).toEqual({ error: 'out_of_range' })
    expect(parseBandwidth('999999999999999', 'MB/s', MIN, MAX)).toEqual({ error: 'out_of_range' })
  })

  it('treats 0.127 Mbit/s (15,875 B/s) as too small', () => {
    expect(parseBandwidth('0,127', 'Mbit/s', MIN, MAX)).toEqual({ error: 'out_of_range' })
  })

  it.each(['', 'abc', '-1', '1e3', '1,', ',5', '1 000', '1,1234567'])('rejects %j as invalid', text => {
    expect(parseBandwidth(text, 'Mbit/s', MIN, MAX)).toEqual({ error: 'invalid' })
  })

  it('rejects a fraction of a byte instead of rounding', () => {
    expect(parseBandwidth('16000,5', 'B/s', MIN, MAX)).toEqual({ error: 'invalid' })
    expect(parseBandwidth('0,0000015', 'MB/s', MIN, MAX)).toEqual({ error: 'invalid' })
  })

  it('shows 1,250,000 B/s as 10 Mbit/s and keeps byte units explicit', () => {
    expect(formatBandwidth(1_250_000)).toBe('10 Mbit/s')
    expect(formatBandwidth(1_000_000)).toBe('8 Mbit/s')
    expect(formatBandwidth(10_000_000)).toBe('80 Mbit/s')
    expect(formatBandwidth(16_000)).toBe('16 KB/s')
    expect(formatBandwidth(16_001)).toBe('16.001 B/s')
    expect(formatBandwidth(100_000_000_000)).toBe('800.000 Mbit/s')
    expect(formatExactRate(1_250_000)).toBe('1.250.000 B/s')
  })

  it('round-trips representative values, including non-round ones', () => {
    for (const bytes of [16_000, 16_001, 125_000, 1_250_000, 1_000_000, 1_500_000, 123_456_789, MAX]) {
      const { text, unit } = toInput(bytes)
      expect(parseBandwidth(text, unit, MIN, MAX)).toEqual({ bytesPerSecond: bytes })
    }
    expect(toInput(1_250_000)).toEqual({ text: '10', unit: 'Mbit/s' })
  })
})
