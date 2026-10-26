import { currentLocale, tx } from '@/lib/i18n'

const MISSING = '—'

function localeNumber(value: number) {
  return value.toLocaleString(currentLocale(), { maximumFractionDigits: 2 })
}

/** One friendly size unit: decimal GB, falling back to MB as the smallest unit (never KB or bytes). Exact byte counts belong in technical details. */
export function formatBytes(bytes: number): string {
  const value = Math.max(0, bytes)
  if (value >= 1_000_000_000) return `${localeNumber(Math.round(value / 10_000_000) / 100)} GB`
  const roundedMb = Math.round(value / 10_000) / 100
  return roundedMb >= 1000 ? `${localeNumber(1)} GB` : `${localeNumber(roundedMb)} MB`
}

function toDate(value: string | number | Date | null | undefined): Date | null {
  if (value == null || value === '') return null
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? null : date
}

/** The exact date and time; `fallback` is shown when the value is missing or invalid. */
export function formatDateTime(value: string | number | Date | null | undefined, fallback = MISSING): string {
  const date = toDate(value)
  return date ? new Intl.DateTimeFormat(currentLocale(), { dateStyle: 'medium', timeStyle: 'short' }).format(date) : fallback
}

const relativeUnits: readonly [Intl.RelativeTimeFormatUnit, number][] = [
  ['year', 365 * 86_400], ['month', 30 * 86_400], ['day', 86_400], ['hour', 3600], ['minute', 60], ['second', 1],
]

/** Recency in everyday words ("3 saat önce"); the exact time is `formatDateTime`. */
export function formatRelativeTime(value: string | number | Date | null | undefined, now: Date = new Date()): string {
  const date = toDate(value)
  if (!date) return MISSING
  const seconds = (date.getTime() - now.getTime()) / 1000
  const [unit, size] = relativeUnits.find(([, size]) => Math.abs(seconds) >= size) ?? ['second', 1]
  // "3 saat önce" always counts; only the sub-minute case reads as "şimdi".
  if (unit === 'second') return new Intl.RelativeTimeFormat(currentLocale(), { numeric: 'auto' }).format(Math.abs(seconds) < 45 ? 0 : Math.trunc(seconds), unit)
  return new Intl.RelativeTimeFormat(currentLocale(), { numeric: 'always' }).format(Math.trunc(seconds / size), unit)
}

export function formatSeconds(seconds: number): string {
  const total = Math.round(seconds)
  const hours = Math.floor(total / 3600)
  const minutes = Math.floor((total % 3600) / 60)
  return tx("{0}{1} dk {2} sn", { "0": hours ? tx("{0} sa ", { "0": hours }) : '', "1": minutes, "2": total % 60 })
}

/** Elapsed time between two instants; still running when `end` is missing. */
export function formatDuration(start?: string | null, end?: string | null): string {
  if (!start) return MISSING
  if (!end) return tx("{0} (devam ediyor)", { "0": formatSeconds(Math.max(0, (Date.now() - Date.parse(start)) / 1000)) })
  return formatSeconds(Math.max(0, (Date.parse(end) - Date.parse(start)) / 1000))
}
