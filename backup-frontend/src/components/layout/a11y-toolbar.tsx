import { currentLocale } from '@/lib/i18n'
import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { Contrast } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { FONT_SCALES, applyA11yPrefs, readFontScale, readHighContrast } from '@/lib/a11y-prefs'

/** Persistent text-size and high-contrast controls shown in every shell top bar. */
export function A11yToolbar() {
  useLanguage()
  const [scale, setScale] = useState(readFontScale)
  const [highContrast, setHighContrast] = useState(readHighContrast)
  const index = FONT_SCALES.indexOf(scale)

  function update(nextScale: typeof scale, nextContrast: boolean) {
    setScale(nextScale)
    setHighContrast(nextContrast)
    applyA11yPrefs(nextScale, nextContrast)
  }

  return <div role="group" aria-label={tx("Erişilebilirlik araçları")} className="flex items-center gap-1 rounded-lg border border-border bg-surface-2 p-0.5">
    <Button variant="ghost" size="icon-sm" aria-label={tx("Yazıyı küçült")} disabled={index === 0} onClick={() => update(FONT_SCALES[index - 1], highContrast)}>A-</Button>
    <span className="min-w-10 text-center text-xs font-medium text-text-muted" aria-live="polite">{tx('%{0}', { '0': scale.toLocaleString(currentLocale()) })}</span>
    <Button variant="ghost" size="icon-sm" aria-label={tx("Yazıyı büyüt")} disabled={index === FONT_SCALES.length - 1} onClick={() => update(FONT_SCALES[index + 1], highContrast)}>A+</Button>
    <Button variant={highContrast ? 'default' : 'ghost'} size="icon-sm" aria-label={tx("Yüksek kontrast")} aria-pressed={highContrast} onClick={() => update(scale, !highContrast)}><Contrast /></Button>
  </div>
}
