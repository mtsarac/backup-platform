import { createContext } from 'react'
import type { SupportedLanguage } from './types'

export interface LanguageContextValue {
  language: SupportedLanguage
  setLanguage: (lang: SupportedLanguage) => void
}

export const LanguageContext = createContext<LanguageContextValue | null>(null)
