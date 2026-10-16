import { createContext } from 'react'
import type { SupportedLanguage, TranslationKey } from './types'

export interface LanguageContextValue {
  language: SupportedLanguage
  setLanguage: (lang: SupportedLanguage) => void
  t: (key: TranslationKey, params?: Record<string, string | number>) => string
}

export const LanguageContext = createContext<LanguageContextValue | null>(null)
