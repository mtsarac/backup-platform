import { tx, useLanguage } from '@/lib/i18n'

export function NotFound() {
  useLanguage()
  return <p>{tx("Sayfa bulunamadı.")}</p>
}
