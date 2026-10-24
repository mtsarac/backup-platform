import { useLayoutEffect, useMemo, useSyncExternalStore, type ReactNode } from 'react'
import { LanguageContext, type LanguageContextValue } from './context-def'
import { changeLanguage, currentLanguage, subscribeLanguage, syncLanguagePreference } from './runtime'

export function LanguageProvider({ children }: { children: ReactNode }) {
  const language = useSyncExternalStore(subscribeLanguage, currentLanguage, () => 'tr' as const)
  useLayoutEffect(syncLanguagePreference, [])
  useLayoutEffect(() => { document.documentElement.lang = language }, [language])
  const value = useMemo<LanguageContextValue>(() => ({ language, setLanguage: changeLanguage }), [language])
  return <LanguageContext.Provider value={value}>{children}</LanguageContext.Provider>
}
