import { describe, expect, it } from 'vitest'
import { sourceCatalog } from './source-catalog'

const sources = import.meta.glob<string>(['/src/**/*.{ts,tsx}', '!/src/**/*.test.{ts,tsx}', '!/src/test/**'], { query: '?raw', import: 'default', eager: true })
// A literal directly after `tx(`; ternaries, concatenations and variables are not statically checkable and are skipped.
const literalCall = /\btx\(\s*('(?:[^'\\\n]|\\.)*'|"(?:[^"\\\n]|\\.)*")/g

describe('i18n source catalog', () => {
  it('has every tx() string literal', () => {
    const missing: string[] = []
    for (const [file, source] of Object.entries(sources)) {
      for (const [, literal] of source.matchAll(literalCall)) {
        const text = new Function(`return ${literal}`)() as string
        if (!(text in sourceCatalog)) missing.push(`${file}: ${text}`)
      }
    }
    expect(Object.keys(sources).length).toBeGreaterThan(100)
    expect(missing).toEqual([])
  })

  it('has a non-empty Turkish and English text for every entry', () => {
    const empty = Object.entries(sourceCatalog).filter(([, text]) => !text.en.trim() || !text.tr.trim()).map(([key]) => key)
    expect(empty).toEqual([])
  })
})
