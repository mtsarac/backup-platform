import { useLayoutEffect, useMemo, useSyncExternalStore, type ReactNode } from 'react'
import { LanguageContext, type LanguageContextValue } from './context-def'
import { translations } from './translations'
import { changeLanguage, currentLanguage, subscribeLanguage, syncLanguagePreference, tx } from './runtime'

export function LanguageProvider({ children }: { children: ReactNode }) {
  const language = useSyncExternalStore(subscribeLanguage, currentLanguage, () => 'tr' as const)
  useLayoutEffect(syncLanguagePreference, [])
  useLayoutEffect(() => { document.documentElement.lang = language }, [language])
  const value = useMemo<LanguageContextValue>(() => ({
    language,
    setLanguage: changeLanguage,
    t: (key, params) => {
      let text = tx(translations[language][key])
      if (params) text = text.replace(/\{(\w+)\}/g, (placeholder, name: string) => String(params[name] ?? placeholder))
      return text
    },
  }), [language])
  return <LanguageContext.Provider value={value}>{children}</LanguageContext.Provider>
}
