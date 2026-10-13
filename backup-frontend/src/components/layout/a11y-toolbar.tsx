import { useState } from 'react'
import { Contrast } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { FONT_SCALES, applyA11yPrefs, readFontScale, readHighContrast } from '@/lib/a11y-prefs'

/** Persistent text-size and high-contrast controls shown in every shell top bar. */
export function A11yToolbar() {
  const [scale, setScale] = useState(readFontScale)
  const [highContrast, setHighContrast] = useState(readHighContrast)
  const index = FONT_SCALES.indexOf(scale)

  function update(nextScale: typeof scale, nextContrast: boolean) {
    setScale(nextScale)
    setHighContrast(nextContrast)
    applyA11yPrefs(nextScale, nextContrast)
  }

  return <div role="group" aria-label="Erişilebilirlik araçları" className="flex items-center gap-1">
    <Button variant="outline" size="icon" aria-label="Yazıyı küçült" disabled={index === 0} onClick={() => update(FONT_SCALES[index - 1], highContrast)}>A-</Button>
    <span className="min-w-12 text-center text-sm font-medium" aria-live="polite">%{scale}</span>
    <Button variant="outline" size="icon" aria-label="Yazıyı büyüt" disabled={index === FONT_SCALES.length - 1} onClick={() => update(FONT_SCALES[index + 1], highContrast)}>A+</Button>
    <Button variant={highContrast ? 'default' : 'outline'} size="icon" aria-label="Yüksek kontrast" aria-pressed={highContrast} onClick={() => update(scale, !highContrast)}><Contrast /></Button>
  </div>
}
