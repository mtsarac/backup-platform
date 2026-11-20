import { describe, expect, it } from 'vitest'
import { foldSearch } from './fold-search'

/** The same samples and answers as the backend's fold test (ListQueryExtensions): the two folds must agree. */
const foldSamples: [string, string][] = [
  ['INDIGO', 'indigo'], ['indigo', 'indigo'], ['İNDİGO', 'indigo'], ['ındıgo', 'indigo'],
  ['Şifreli', 'sifreli'], ['SIFRELI', 'sifreli'], ['sifreli', 'sifreli'], ['ŞİFRELİ', 'sifreli'],
  ['WIN11', 'win11'], ['win11', 'win11'],
  ['Çağrı', 'cagri'], ['CAĞRI', 'cagri'], ['ÖĞÜT', 'ogut'], ['öğüt', 'ogut'],
  ['100%_literal', '100%_literal'], ['', ''],
]

describe('foldSearch', () => {
  it.each(foldSamples)('folds %s to %s', (text, folded) => {
    expect(foldSearch(text)).toBe(folded)
  })

  it('drops the combining dot of a decomposed İ', () => {
    expect(foldSearch('I\u0307ndigo')).toBe('indigo')
  })
})
