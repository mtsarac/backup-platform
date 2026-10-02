import { useEffect, useState, type ReactNode } from 'react'
import { ThemeContext, type ResolvedTheme, type Theme } from './theme-context'

function resolve(theme: Theme): ResolvedTheme {
  if (theme !== 'system') return theme
  return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light'
}

export function ThemeProvider({ children }: { children: ReactNode }) {
  const [theme, setThemeState] = useState<Theme>(() => {
    const stored = localStorage.getItem('backup-theme')
    return (stored as Theme) ?? 'system'
  })

  const resolvedTheme = resolve(theme)

  useEffect(() => {
    document.documentElement.classList.toggle('dark', resolvedTheme === 'dark')
  }, [resolvedTheme])

  const setTheme = (next: Theme) => {
    localStorage.setItem('backup-theme', next)
    setThemeState(next)
  }

  const toggleTheme = () => setTheme(resolvedTheme === 'light' ? 'dark' : 'light')

  return (
    <ThemeContext.Provider value={{ theme, resolvedTheme, setTheme, toggleTheme }}>
      {children}
    </ThemeContext.Provider>
  )
}
