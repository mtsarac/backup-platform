import { Languages } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { useLanguage } from '@/lib/i18n'

export function LanguageSwitcher() {
  const { language, setLanguage, t } = useLanguage()


  return (
    <Button
      variant="ghost"
      size="sm"
      className="flex items-center gap-1.5 px-2.5 font-medium text-xs text-text-muted hover:text-text"
      onClick={() => setLanguage(language === 'tr' ? 'en' : 'tr')}
      aria-label={t('shell.switchLang')}
      title={t('shell.switchLang')}
    >
      <Languages className="size-4" />
      <span className={language === 'tr' ? 'font-bold text-primary' : 'opacity-60'}>TR</span>
      <span className="opacity-40">/</span>
      <span className={language === 'en' ? 'font-bold text-primary' : 'opacity-60'}>EN</span>
    </Button>
  )
}
