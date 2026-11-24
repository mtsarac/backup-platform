import { describe, expect, it } from 'vitest'
import { sourceCatalog } from '@/lib/i18n/source-catalog'
import { actionName } from './accessible-name'

const sources = import.meta.glob<string> /* v4 */(['/src/**/*.tsx', '!/src/**/*.test.tsx'], { query: '?raw', import: 'default', eager: true })
const lower = (text: string) => text.replaceAll('İ', 'i').replaceAll('I', 'ı').toLowerCase()
const trOf = (key: string) => sourceCatalog[key]?.tr ?? key
const quoted = String.raw`(["'])((?:(?!\1).)*)\1`

/** End of the opening tag that starts at `from`: the first `>` outside braces and quotes. */
function tagEnd(source: string, from: number) {
  let depth = 0
  for (let i = from; i < source.length; i++) {
    const char = source[i]
    if (char === '{') depth++
    else if (char === '}') depth--
    else if ((char === '"' || char === "'" || char === '`') && depth === 0) i = source.indexOf(char, i + 1)
    else if (char === '>' && depth === 0 && source[i - 1] !== '=') return i
  }
  return -1
}

describe('actionName', () => {
  it('opens the name with the visible action, then the subject', () => {
    expect(actionName('düzenle', 'Ayşe')).toBe('Düzenle — Ayşe')
    expect(actionName('izin', 'x')).toBe('İzin — x')
  })
})

describe('WCAG 2.5.3 label in name', () => {
  it('every button or link with visible text and an aria-label starts that label with the visible text', () => {
    const offenders: string[] = []
    for (const [file, source] of Object.entries(sources)) {
      if (file.endsWith('/copy-button.tsx')) continue
      for (const open of source.matchAll(/<(Button|button|Link|a)\b/g)) {
        const end = tagEnd(source, open.index + open[0].length)
        const tag = source.slice(open.index, end)
        if (!tag.includes('aria-label=') || /role=["']switch["']/.test(tag)) continue
        const body = source.slice(end + 1, source.indexOf(`</${open[1]}>`, end))
        const visible = body.match(new RegExp(String.raw`tx\(${quoted}`))?.[2]
        if (!visible) continue
        const label = tag.match(/aria-label=(\{[^\n]*)/)?.[1] ?? ''
        if (/^\{(actionName|label)\(/.test(label)) continue
        const literal = label.match(new RegExp(String.raw`^\{tx\(${quoted}`))?.[2]
        if (!literal || !lower(trOf(literal)).startsWith(lower(trOf(visible)))) offenders.push(`${file}: "${visible}" vs ${label.slice(0, 70)}`)
      }
    }
    expect(offenders).toEqual([])
  })
})
