import { useEffect } from 'react'
import { tx, useLanguage } from '@/lib/i18n'

/** Sets the browser tab title to "<page> · Yedekleme Platformu" (the product name alone without a page title). */
export function usePageTitle(title?: string) {
  const { language } = useLanguage()
  useEffect(() => {
    document.title = title ? `${title} · ${tx('Yedekleme Platformu')}` : tx('Yedekleme Platformu')
  }, [title, language])
}
