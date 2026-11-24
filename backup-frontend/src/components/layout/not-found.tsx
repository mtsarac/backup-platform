import { Link } from 'react-router-dom'
import { linkClass } from '@/components/ui/form-styles'
import { tx, useLanguage } from '@/lib/i18n'

/** `standalone` fills the page when no shell surrounds it (an unknown workspace). */
export function NotFound({ standalone = false }: { standalone?: boolean }) {
  useLanguage()
  const content = <div className="mx-auto max-w-lg space-y-3 p-6 text-center">
    <h1 className="text-lg font-semibold">{tx("Sayfa bulunamadı.")}</h1>
    <Link className={linkClass} to="/">{tx("Ana sayfaya dön")}</Link>
  </div>
  return standalone ? <main className="flex min-h-svh items-center justify-center bg-surface-2">{content}</main> : content
}
