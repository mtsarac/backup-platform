import { tx } from '@/lib/i18n'
import { useContext } from 'react'
import { ThemeContext } from '@/components/theme-context'

export function useTheme() {
  const ctx = useContext(ThemeContext)
  if (!ctx) throw new Error(tx('useTheme must be used within ThemeProvider'))
  return ctx
}
