import { tx, useLanguage } from '@/lib/i18n'
import { ShieldAlert } from 'lucide-react'
import { Link } from 'react-router-dom'

export function PlatformAccessDenied({ standalone = false, description = tx('Platform Administration yalnızca PlatformAdmin yetkisi olan kullanıcılar içindir. Tenant Owner / Admin rolü platform erişimi vermez.') }: { standalone?: boolean; description?: string }) {
  useLanguage()
  const content = <div role="alert" className="mx-auto max-w-lg space-y-3 rounded-lg border border-error/30 bg-error-subtle p-6 text-error">
    <div className="flex items-center gap-2"><ShieldAlert className="size-5" aria-hidden="true" /><h1 className="text-lg font-semibold">{tx("Bu alan için yetkiniz yok")}</h1></div>
    <p className="text-sm">{description}</p>
    <Link className="inline-block text-sm font-medium underline" to="/">{tx("Ana sayfaya dön")}</Link>
  </div>
  return standalone ? <main className="flex min-h-svh items-center justify-center bg-surface-2 p-6">{content}</main> : content
}
