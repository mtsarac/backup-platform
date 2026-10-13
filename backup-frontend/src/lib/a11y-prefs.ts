const SIZE_KEY = 'backup-font-scale'
const CONTRAST_KEY = 'backup-high-contrast'

export const FONT_SCALES = [100, 125, 150] as const
export type FontScale = (typeof FONT_SCALES)[number]

export function readFontScale(): FontScale {
  const stored = Number(localStorage.getItem(SIZE_KEY))
  return FONT_SCALES.find((scale) => scale === stored) ?? 100
}

export function readHighContrast() {
  return localStorage.getItem(CONTRAST_KEY) === '1'
}

export function applyA11yPrefs(scale = readFontScale(), highContrast = readHighContrast()) {
  const root = document.documentElement
  root.style.fontSize = `${scale}%`
  root.classList.toggle('hc', highContrast)
  localStorage.setItem(SIZE_KEY, String(scale))
  localStorage.setItem(CONTRAST_KEY, highContrast ? '1' : '0')
}
