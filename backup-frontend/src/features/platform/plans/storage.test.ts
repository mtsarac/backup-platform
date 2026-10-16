import { describe, expect, it } from 'vitest'
import { formatBytes, formatGb, MAX_STORAGE_BYTES, parseGb, toGbInput } from './storage'

describe('decimal storage units (1 GB = 1,000,000,000 bytes)', () => {
  it('formats capacity as decimal GB, never binary', () => {
    expect(formatGb(5_000_000_000)).toBe('5 GB')
    expect(formatGb(2_500_000_000)).toBe('2,5 GB')
    expect(formatGb(1_000_000_000)).toBe('1 GB')
    expect(formatGb(5_368_709_120)).toBe('5,36870912 GB')
    expect(formatGb(null)).toBe('Sınırsız')
  })

  it('shows exact bytes with Turkish grouping', () => {
    expect(formatBytes(5_000_000_000)).toBe('5.000.000.000 bayt')
  })

  it.each([
    ['5', 5_000_000_000], ['2,5', 2_500_000_000], ['2.5', 2_500_000_000], ['0,001', 1_000_000], ['0.1', 100_000_000],
    ['1,125', 1_125_000_000], [' 20 ', 20_000_000_000], ['1000000', MAX_STORAGE_BYTES], ['999999,999', 999_999_999_000_000],
  ])('parses %j to %d bytes exactly', (text, bytes) => {
    expect(parseGb(text)).toEqual({ bytes })
  })

  it.each(['', 'abc', '1,2345', '-1', '1e3', '1,', ',5', '1 000'])('rejects %j as invalid', (text) => {
    expect(parseGb(text)).toEqual({ error: 'invalid' })
  })

  it.each(['0', '0,000', '1000000,001', '999999999'])('rejects %j as out of range', (text) => {
    expect(parseGb(text)).toEqual({ error: 'out_of_range' })
  })

  it('round-trips every milli-GB step without float drift', () => {
    for (let milli = 1; milli <= 5000; milli++) {
      const bytes = milli * 1_000_000
      expect(parseGb(toGbInput(bytes))).toEqual({ bytes })
    }
    expect(parseGb(toGbInput(MAX_STORAGE_BYTES))).toEqual({ bytes: MAX_STORAGE_BYTES })
  })
})
