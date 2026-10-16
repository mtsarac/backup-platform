import { tx } from '@/lib/i18n'
import { currentLocale } from '@/lib/i18n'


/** Decimal (SI) steps: 1 GB = 1,000,000,000 bytes. Smallest unit is MB (1 MB = 1,000,000 bytes). */
const units = [
  { suffix: 'GB', size: 1_000_000_000 },
  { suffix: 'MB', size: 1_000_000 },
] as const

function number(value: number) {
  return value.toLocaleString(currentLocale(), { maximumFractionDigits: 2 })
}

/**
 * Capacity for display: decimal GB (`6,2 GB`), falling back to MB as the smallest unit (never KB or bayt).
 * Display rounding only (2 decimals).
 */
export function formatCapacity(bytes: number): string {
  const value = Math.max(0, bytes)
  if (value >= 1_000_000_000) {
    const rounded = Math.round((value / 1_000_000_000) * 100) / 100
    return `${number(rounded)} GB`
  }
  const roundedMb = Math.round((value / 1_000_000) * 100) / 100
  if (roundedMb >= 1000) return `${number(1)} GB`
  return `${number(roundedMb)} MB`
}

/** Formats capacity with MB as the smallest unit. */
export function formatExactBytes(bytes: number): string {
  return formatCapacity(bytes)
}

/**
 * Share of the limit that is committed (used + reserved), as a whole percent for the bar and caption. Display only: the quota *state* always
 * comes from the backend. Rounded down and never 100 while capacity remains, so "100%" always means the limit is reached.
 */
export function allocatedPercent(usedBytes: number, reservedBytes: number, limitBytes: number | null): number | null {
  if (limitBytes === null || limitBytes <= 0) return null
  const committed = Math.max(0, usedBytes) + Math.max(0, reservedBytes)
  if (committed >= limitBytes) return 100
  return Math.min(99, Math.floor((committed / limitBytes) * 100))
}
