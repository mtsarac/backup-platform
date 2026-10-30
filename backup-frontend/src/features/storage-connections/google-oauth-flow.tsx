import { Notice } from '@/components/ui/notice'
import { tx } from '@/lib/i18n'
import type { GoogleOAuthOutcome } from './use-google-oauth'

/** The result of the consent itself; the connection test result is shown by the caller right below it. */
export function GoogleOAuthNotice({ outcome }: { outcome: GoogleOAuthOutcome }) {
  if (outcome.status === 'connected') {
    const { accountName, accountEmail } = outcome.account.configuration ?? {}
    const account = accountName && accountEmail ? `${accountName} (${accountEmail})` : accountName || accountEmail
    return <Notice tone="success" title={account ? tx("Google hesabı bağlandı: {0}", { "0": account }) : tx("Google hesabı bağlandı")} />
  }
  if (outcome.status === 'incomplete') {
    return <Notice tone="warning" title={tx("Google yetkilendirmesi tamamlanmadı")}>{tx("Google penceresi bağlantı tamamlanmadan kapandı ya da süre doldu. Yeniden deneyin ve izni onaylayın; Google hesabının uygulamada test kullanıcısı olarak eklenmiş olması gerekir.")}</Notice>
  }
  return null
}
