import { createRequire } from 'node:module'
import { describe, expect, it } from 'vitest'

const color = createRequire(import.meta.url)('./color.cjs')

describe('a11y colour maths', () => {
  it('parses rgb, rgba, hex and transparent without scaling the channels by alpha', () => {
    expect(color.parse('rgb(255, 255, 255)')).toEqual([255, 255, 255, 1])
    expect(color.parse('rgba(255, 255, 255, 0.95)')).toEqual([255, 255, 255, 0.95])
    expect(color.parse('rgb(15 94 99 / 40%)')).toEqual([15, 94, 99, 0.4])
    expect(color.parse('#0f5e63')).toEqual([15, 94, 99, 1])
    expect(color.parse('transparent')).toEqual([0, 0, 0, 0])
  })

  it('composites translucent colours inside 0..255', () => {
    expect(color.over([0, 0, 0, 0.5], [255, 255, 255, 1])).toEqual([127.5, 127.5, 127.5, 1])
    const nearOpaque = color.over([255, 255, 255, 0.95], [255, 255, 255, 1])
    expect(Math.max(...nearOpaque.slice(0, 3))).toBeLessThanOrEqual(255)
    expect(color.over([10, 20, 30, 0], [200, 100, 50, 1])).toEqual([200, 100, 50, 1])
  })

  it('gives the WCAG extremes 21:1 and 1:1 and never more', () => {
    expect(color.ratio([0, 0, 0, 1], [255, 255, 255, 1])).toBe(21)
    expect(color.ratio([255, 255, 255, 1], [255, 255, 255, 1])).toBe(1)
    expect(color.ratio([0x0f, 0x5e, 0x63, 1], [255, 255, 255, 1])).toBe(7.49)
  })

  it('rejects impossible values instead of reporting them', () => {
    expect(() => color.assertColor([268, 268, 268, 1], 'x')).toThrow(RangeError)
    expect(() => color.parse('rgb(300, 0, 0)')).toThrow(RangeError)
    expect(() => color.ratio([0, 0, 0, 0.5], [255, 255, 255, 1])).toThrow(RangeError)
  })

  it('validateDeep flags out-of-range colours and ratios in a results tree', () => {
    expect(color.validateDeep({ ok: { ratio: 21, background: 'rgb(255, 255, 255)' } })).toEqual([])
    const problems = color.validateDeep({ a: { ratio: 23.4, background: 'rgb(268,268,268)', contrastRatio: 0.5 } })
    expect(problems).toHaveLength(3)
  })
})
