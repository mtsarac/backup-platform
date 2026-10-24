import { currentLocale } from '@/lib/i18n'
import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { Popover } from '@base-ui/react/popover'
import { Contrast, Moon, Settings2 } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { useTheme } from '@/hooks/use-theme'
import { FONT_SCALES, applyA11yPrefs, readFontScale, readHighContrast } from '@/lib/a11y-prefs'

/** All persistent display preferences share one entry point in both shell top bars. */
export function A11yToolbar() {
  useLanguage()
  const { resolvedTheme, toggleTheme } = useTheme()
  const [scale, setScale] = useState(readFontScale)
  const [highContrast, setHighContrast] = useState(readHighContrast)
  const index = FONT_SCALES.indexOf(scale)

  function update(nextScale: typeof scale, nextContrast: boolean) {
    setScale(nextScale)
    setHighContrast(nextContrast)
    applyA11yPrefs(nextScale, nextContrast)
  }

  return <Popover.Root>
    <Popover.Trigger render={<Button variant="outline" />}>
      <Settings2 aria-hidden="true" />{tx("Görünüm")}
    </Popover.Trigger>
    <Popover.Portal>
      <Popover.Positioner sideOffset={8} align="end" className="z-50">
        <Popover.Popup className="max-h-[var(--available-height)] w-80 max-w-[calc(100vw-2rem)] space-y-3 overflow-y-auto rounded-xl border border-border bg-surface p-4 text-text shadow-lg">
          <Popover.Title className="font-semibold text-text-heading">{tx("Görünüm")}</Popover.Title>
          <Button className="h-auto w-full whitespace-normal py-2" disabled={index === FONT_SCALES.length - 1} onClick={() => update(FONT_SCALES[index + 1], highContrast)}>
            <span aria-hidden="true" className="text-lg font-bold">{tx("A+")}</span>{tx("Yazıyı büyüt")}
          </Button>
          <div className="flex flex-wrap items-center justify-between gap-3">
            <Button variant="outline" onClick={() => update(FONT_SCALES[index - 1], highContrast)} disabled={index === 0}>{tx("Yazıyı küçült")}</Button>
            <span role="status" className="text-sm font-medium">{tx('%{0}', { '0': scale.toLocaleString(currentLocale()) })}</span>
          </div>
          <Button variant="outline" className="w-full justify-start" aria-pressed={highContrast} onClick={() => update(scale, !highContrast)}>
            <Contrast aria-hidden="true" />{tx("Yüksek kontrast")}
          </Button>
          <Button variant="outline" className="w-full justify-start" aria-label={tx("Temayı değiştir")} aria-pressed={resolvedTheme === 'dark'} onClick={toggleTheme}>
            <Moon aria-hidden="true" />{tx("Koyu tema")}
          </Button>
        </Popover.Popup>
      </Popover.Positioner>
    </Popover.Portal>
  </Popover.Root>
}
