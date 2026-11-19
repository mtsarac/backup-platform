import { tx } from '@/lib/i18n'

const browsers: [RegExp, string][] = [[/Edg(e|A|iOS)?\//, 'Edge'], [/OPR\/|Opera/, 'Opera'], [/Firefox\/|FxiOS\//, 'Firefox'], [/Chrome\/|CriOS\//, 'Chrome'], [/Safari\//, 'Safari']]
const systems: [RegExp, string][] = [[/Windows/, 'Windows'], [/Android/, 'Android'], [/iPhone|iPad|iPod/, 'iOS'], [/Mac OS X|Macintosh/, 'macOS'], [/CrOS/, 'ChromeOS'], [/Linux|X11/, 'Linux']]

/** Friendly "Chrome · Windows" name from a raw user agent; a tiny heuristic, not a full parser. */
export function deviceName(userAgent: string | null): string {
  const agent = userAgent ?? ''
  const browser = browsers.find(([pattern]) => pattern.test(agent))?.[1]
  const system = systems.find(([pattern]) => pattern.test(agent))?.[1]
  if (!browser && !system) return tx('Bilinmeyen cihaz')
  const mobile = /Mobile|iPhone|iPad|Android/.test(agent) && browser ? `${tx('Mobil')} ` : ''
  return [browser && mobile + browser, system].filter(Boolean).join(' · ')
}
