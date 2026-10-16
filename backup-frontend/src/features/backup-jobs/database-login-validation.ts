import { tx } from '@/lib/i18n'
import type { DatabaseLogin } from '@/features/backup-jobs/types'

/** A typed login must be complete; an empty one is valid only when a stored login can be kept. */
export function loginErrors(login: DatabaseLogin | null | undefined, hasStored: boolean): { userName?: string; password?: string } | undefined {
  if (!login) return hasStored ? undefined : { userName: tx("Veritabanı kullanıcı adını girin."), password: tx("Veritabanı parolasını girin.") }
  return {
    userName: login.userName.trim() ? undefined : tx("Veritabanı kullanıcı adını girin."),
    password: login.password ? undefined : tx("Veritabanı parolasını girin."),
  }
}
