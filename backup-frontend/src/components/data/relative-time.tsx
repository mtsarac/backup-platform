import { useLanguage } from '@/lib/i18n'
import { formatDateTime, formatRelativeTime } from '@/lib/format'

/** "3 saat önce", with the exact time on hover. */
export function RelativeTime({ value }: { value: string | null | undefined }) {
  useLanguage()
  if (!value || Number.isNaN(Date.parse(value))) return <span>—</span>
  return <time dateTime={value} title={formatDateTime(value)}>{formatRelativeTime(value)}</time>
}
