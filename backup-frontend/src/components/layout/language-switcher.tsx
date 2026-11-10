import { Languages } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { tx, useLanguage } from '@/lib/i18n'

// Active language: weight and underline, not only colour, so it survives forced-colors. Inactive text keeps the full muted token; opacity would dim it below 4.5:1.
const ACTIVE = 'font-bold text-primary underline decoration-2 underline-offset-4'

export function LanguageSwitcher() {
  const { language, setLanguage } = useLanguage()

  return (
    <Button
      variant="ghost"
      size="sm"
      className="flex min-h-9 pointer-coarse:min-h-11 items-center gap-1.5 px-2.5 font-medium text-xs text-text-muted hover:text-text"
      onClick={() => setLanguage(language === 'tr' ? 'en' : 'tr')}
      aria-label={`${tx("Dili değiştir")}: TR / EN`}
      title={tx("Dili değiştir")}
    >
      <Languages className="size-4" />
      <span aria-current={language === 'tr' ? 'true' : undefined} className={language === 'tr' ? ACTIVE : undefined}>TR</span>
      <span aria-hidden="true">/</span>
      <span aria-current={language === 'en' ? 'true' : undefined} className={language === 'en' ? ACTIVE : undefined}>EN</span>
    </Button>
  )
}
