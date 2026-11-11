/// <reference types="node" />
import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { describe, expect, it } from 'vitest'
import { sourceCatalog } from './source-catalog'

const sources = import.meta.glob<string> /* v4 */(['/src/**/*.{ts,tsx}', '!/src/**/*.test.{ts,tsx}', '!/src/test/**', '!/src/lib/i18n/source-catalog.ts'], { query: '?raw', import: 'default', eager: true })
const references = import.meta.glob<string> /* v4 */(['/src/**/*.{ts,tsx}', '!/src/lib/i18n/source-catalog.ts'], { query: '?raw', import: 'default', eager: true })
const literal = String.raw`(?:'(?:[^'\\\n]|\\.)*'|"(?:[^"\\\n]|\\.)*"|` + '`(?:[^`\\\\\\n$]|\\\\.)*`' + ')'
const unquote = (quoted: string) => new Function(`return ${quoted}`)() as string
/** Technical text shown exactly as written: example values, identifiers and product names. */
const verbatim: readonly string[] = ['localhost', 'RESTORE VERIFYONLY', 'Europe/Istanbul', 'https://backup.example.com', 'managed', 'central, local', 'managed, tenant', 'MSSQL', 'PostgreSQL', 'Windows', 'Linux', 'central', 'local', 'TR', 'EN']
/** Texts the backend supplies (storage provider descriptors); the UI looks them up with a variable, so no source literal names them. */
const backendSupplied: readonly string[] = ['Disk adı (agent\'ta tanımlı)', 'Private key passphrase', 'Hesap adı', 'S3-compatible object storage', 'SMB / NAS (encrypted SMB 3)', 'Physical site / failure domain', 'Media class (disk, object-storage, tape)', 'HTTPS endpoint', 'Signing region', 'Object Lock (COMPLIANCE or GOVERNANCE)', 'WORM retention days', 'Session token', 'NAS host', 'Share name', 'Domain']

/** First-argument literals, including ternary, ?? and plain template literals; nested calls and interpolated templates are skipped. */
function txLiterals(source: string): string[] {
  const found: string[] = []
  const quoted = new RegExp(literal, 'y')
  for (const call of source.matchAll(/\btx\(/g)) {
    let depth = 1
    for (let i = call.index + call[0].length; i < source.length && depth > 0; i++) {
      const char = source[i]
      if (char === "'" || char === '"' || char === '`') {
        quoted.lastIndex = i
        const match = quoted.exec(source)
        if (!match) {
          if (char !== '`') break
          i = source.indexOf('`', i + 1)
          if (i < 0) break
          continue
        }
        if (depth === 1) found.push(unquote(match[0]))
        i += match[0].length - 1
      } else if ('([{'.includes(char)) depth++
      else if (')]}'.includes(char)) depth--
      else if (char === ',' && depth === 1) break
      if (i < 0) break
    }
  }
  return found
}

function hasReference(key: string, source: string): boolean {
  const escaped = key.replaceAll('\\', '\\\\')
  return source.includes(JSON.stringify(key)) || source.includes(`'${escaped.replaceAll("'", "\\'")}'`) ||
    source.includes('`' + escaped.replaceAll('`', '\\`') + '`') || source.includes(`>${key}<`)
}

function bareTexts(source: string): string[] {
  const attribute = /\b(?:aria-label|title|placeholder|alt|label|description|hint)=["']([^"']*\p{L}[^"']*)["']/gu
  const jsxText = />([^<>{}=\n]*\p{L}[^<>{}=\n]*)<\//gu
  const beforeTx = new RegExp(String.raw`\?\s*(${literal})\s*:\s*tx\(`, 'g')
  const afterTx = new RegExp(String.raw`\btx\(${literal}[^()]*\)\s*:\s*(${literal})`, 'g')
  const emptyBranch = new RegExp(String.raw`\?\s*(${literal})\s*:\s*(?:''|"")`, 'g')
  const texts = [...source.matchAll(attribute), ...source.matchAll(jsxText)].map(match => match[1].trim())
  for (const re of [beforeTx, afterTx]) for (const match of source.matchAll(re)) texts.push(unquote(match[1]))
  for (const match of source.matchAll(emptyBranch)) {
    const text = unquote(match[1])
    if (/[A-ZÇĞİÖŞÜçğıöşü…]/.test(text)) texts.push(text)
  }
  return texts.filter(text => /\p{L}/u.test(text) && !verbatim.includes(text))
}

describe('i18n source catalog', () => {
  it('has every tx() string literal, including ternary branches', () => {
    const missing: string[] = []
    for (const [file, source] of Object.entries(sources)) for (const text of txLiterals(source)) if (!(text in sourceCatalog)) missing.push(`${file}: ${text}`)
    expect(Object.keys(sources).length).toBeGreaterThan(100)
    expect(missing).toEqual([])
  })

  it('has a non-empty Turkish and English text for every entry', () => {
    const empty = Object.entries(sourceCatalog).filter(([, text]) => !text.en.trim() || !text.tr.trim()).map(([key]) => key)
    expect(empty).toEqual([])
  })

  it('shows no bare text in attributes, JSX text or conditional branches', () => {
    const bare = Object.entries(sources).flatMap(([file, source]) => bareTexts(source).map(text => `${file}: ${text}`))
    expect(bare).toEqual([])
  })

  it('detects the pending/added marker and attribute regressions, and reads template keys', () => {
    expect(bareTexts(`<button aria-label="Ekle">{pending ? 'Ekleniyor…' : tx('Üye ekle')}</button>`)).toEqual(['Ekle', 'Ekleniyor…'])
    expect(bareTexts(`<option>{alreadyAdded ? '(Eklendi)' : ''}</option>`)).toEqual(['(Eklendi)'])
    expect(txLiterals("tx(`Özet`); tx(ready ? 'Etkin' : 'Devre dışı'); tx(key ?? 'Bilinmiyor')")).toEqual(['Özet', 'Etkin', 'Devre dışı', 'Bilinmiyor'])
  })

  it('keeps no entry that nothing references', () => {
    const source = Object.entries(references).map(([, code]) => code).join('\n')
    expect(Object.keys(sourceCatalog).filter(key => !hasReference(key, source) && !backendSupplied.includes(key))).toEqual([])
  }, 20_000)

  it('translates every text of the backend storage provider descriptors', () => {
    const descriptorSource = readFileSync(resolve(import.meta.dirname, '../../../../backup-backend/src/modules/storage/StorageProviderDescriptor.cs'), 'utf8')
    const texts = [...descriptorSource.matchAll(/(?:new\("\w+", |StorageProviderTypes\.\w+, )"([^"]+)"/g)].map(match => match[1])
    expect(texts.length).toBeGreaterThan(20)
    expect(texts.filter(text => !(text in sourceCatalog))).toEqual([])
  })
})
