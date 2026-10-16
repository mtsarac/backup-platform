import type { SupportedLanguage } from './types'
import { sourceCatalog } from './source-catalog'

const STORAGE_KEY = 'bp_language'
const listeners = new Set<() => void>()
function savedLanguage(): SupportedLanguage {
  try { return localStorage.getItem(STORAGE_KEY) === 'en' ? 'en' : 'tr' } catch { return 'tr' }
}
let language = savedLanguage()

export function currentLanguage(): SupportedLanguage { return language }
export function syncLanguagePreference() {
  const next = savedLanguage()
  if (language === next) return
  language = next
  listeners.forEach(listener => listener())
}
export function currentLocale(): 'tr-TR' | 'en-US' { return currentLanguage() === 'tr' ? 'tr-TR' : 'en-US' }
export function subscribeLanguage(listener: () => void) {
  listeners.add(listener)
  return () => { listeners.delete(listener) }
}
export function changeLanguage(next: SupportedLanguage) {
  language = next
  try { localStorage.setItem(STORAGE_KEY, next) } catch { /* Storage may be unavailable in private browsing. */ }
  document.documentElement.lang = next
  listeners.forEach(listener => listener())
}

/** Source keys are UI copy only; never pass customer data or protocol identifiers. */
export function tx(source: string, params?: Record<string, unknown>): string
export function tx<T>(source: T, params?: Record<string, unknown>): T | string
export function tx(source: unknown, params?: Record<string, unknown>) {
  if (typeof source !== 'string') return source
  const entry = sourceCatalog[source]
  let text = entry ? entry[currentLanguage()] : source
  if (params) text = text.replace(/\{(\w+)\}/g, (placeholder, key: string) => String(params[key] ?? placeholder))
  return text
}
