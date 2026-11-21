const turkish: Record<string, string> = { İ: 'i', I: 'i', ı: 'i', Ş: 's', ş: 's', Ğ: 'g', ğ: 'g', Ç: 'c', ç: 'c', Ö: 'o', ö: 'o', Ü: 'u', ü: 'u' }

/**
 * The one search rule of every list (the backend folds the same way): case-insensitive and Turkish-letter-insensitive.
 * `İ`/`I`/`ı`/`i` all become `i`, and `ş ğ ç ö ü` become `s g c o u`, so `INDIGO`, `İNDİGO` and `indigo` find each other, as do `Şifreli` and `sifreli`.
 * Both the term and the searched text go through it.
 */
export function foldSearch(text: string): string {
  return text.replace(/[İIıŞşĞğÇçÖöÜü]/g, letter => turkish[letter]).toLowerCase().replaceAll('\u0307', '')
}
