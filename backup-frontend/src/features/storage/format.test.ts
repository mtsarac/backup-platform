import { describe, expect, it } from 'vitest'
import { allocatedPercent, formatCapacity, formatExactBytes } from './format'

describe('storage formatting rules', () => {
  it('converts bytes to decimal GB and MB, with MB as smallest unit', () => {
    expect(formatCapacity(1_000_000_000)).toBe('1 GB')
    expect(formatCapacity(5_500_000_000)).toBe('5,5 GB')
    expect(formatCapacity(10_000_000_000)).toBe('10 GB')
    expect(formatCapacity(500_000_000)).toBe('500 MB')
    expect(formatCapacity(250_000)).toBe('0,25 MB')
    expect(formatCapacity(500)).toBe('0 MB')
    expect(formatCapacity(0)).toBe('0 MB')
  })

  it('formats capacity with MB as smallest unit', () => {
    expect(formatExactBytes(1_000_000_000)).toBe('1 GB')
    expect(formatExactBytes(500_000_000)).toBe('500 MB')
    expect(formatExactBytes(0)).toBe('0 MB')
  })

  it('calculates allocated percent accurately against limits', () => {
    // null limit -> null percent
    expect(allocatedPercent(100, 0, null)).toBeNull()
    expect(allocatedPercent(0, 0, null)).toBeNull()

    // 0 used/reserved -> 0%
    expect(allocatedPercent(0, 0, 1_000_000_000)).toBe(0)

    // used + reserved combined
    expect(allocatedPercent(400_000_000, 100_000_000, 1_000_000_000)).toBe(50)

    // exactly full -> 100%
    expect(allocatedPercent(1_000_000_000, 0, 1_000_000_000)).toBe(100)

    // over quota -> capped at 100%
    expect(allocatedPercent(1_200_000_000, 0, 1_000_000_000)).toBe(100)

    // almost full (e.g. 99.8%) does not round to 100% before actually hitting limit
    expect(allocatedPercent(999_000_000, 0, 1_000_000_000)).toBe(99)
  })
})
