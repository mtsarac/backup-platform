import { tx, useLanguage } from '@/lib/i18n'
import { Moon, Sun } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { useTheme } from '@/hooks/use-theme'

/** The only display preference: light or dark. */
export function ThemeToggle() {
  useLanguage()
  const { resolvedTheme, toggleTheme } = useTheme()
  const dark = resolvedTheme === 'dark'
  return <Button variant="outline" size="icon" aria-label={tx("Temayı değiştir")} aria-pressed={dark} onClick={toggleTheme}>
    {dark ? <Sun aria-hidden="true" /> : <Moon aria-hidden="true" />}
  </Button>
}
