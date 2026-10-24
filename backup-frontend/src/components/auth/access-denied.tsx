import { tx, useLanguage } from '@/lib/i18n'
import type { ReactNode } from 'react'
import { Link } from 'react-router-dom'
import { ErrorNotice } from '@/components/ui/notice'
import { accessDenied } from '@/lib/api/failure-messages'
import { linkClass } from '@/components/ui/form-styles'

export const platformOnlyMessage = () => tx('Platform Administration yalnızca PlatformAdmin yetkisi olan kullanıcılar içindir. Tenant Owner / Admin rolü platform erişimi vermez.')

/** The one permission wall: nothing was changed, and the way forward is a person who can grant access. */
export function AccessDenied({ what, title = tx("Bu alan için yetkiniz yok"), standalone = false, control }: {
  what: string; title?: string; standalone?: boolean
  /** Replaces the default way home when a nearer page is the better next step. */
  control?: ReactNode
}) {
  useLanguage()
  const Heading = standalone ? 'h1' : 'h2'
  const notice = <ErrorNotice className="mx-auto max-w-lg" title={<Heading className="text-lg font-semibold">{title}</Heading>} {...accessDenied(what)}
    control={control ?? <Link className={linkClass} to="/">{tx("Ana sayfaya dön")}</Link>} />
  return standalone ? <main className="flex min-h-svh items-center justify-center bg-surface-2 p-6">{notice}</main> : notice
}
