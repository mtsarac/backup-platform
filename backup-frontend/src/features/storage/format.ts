const locale = 'tr-TR'

/** Decimal (SI) steps only: 1 GB = 1,000,000,000 bytes. Never 1024-based, never labelled GiB. */
const units = [
  { suffix: 'GB', size: 1_000_000_000 },
  { suffix: 'MB', size: 1_000_000 },
  { suffix: 'KB', size: 1_000 },
] as const

function number(value: number) {
  return value.toLocaleString(locale, { maximumFractionDigits: 2 })
}

/**
 * Capacity for display: decimal GB (`6,2 GB`), falling back to MB / KB / bayt below 1 GB so a small amount never reads as `0 GB`.
 * Display rounding only (2 decimals); the exact byte count stays available through `formatExactBytes`.
 */
export function formatCapacity(bytes: number): string {
  const value = Math.max(0, bytes)
  for (const [index, unit] of units.entries()) {
    if (value >= unit.size) {
      const rounded = Math.round((value / unit.size) * 100) / 100
      const larger = units[index - 1]
      // 999,999 KB rounds to 1.000 MB, which is better said as 1 GB.
      if (larger && rounded * unit.size >= larger.size) return `${number(1)} ${larger.suffix}`
      return `${number(rounded)} ${unit.suffix}`
    }
  }
  return `${value.toLocaleString(locale)} bayt`
}

/** `5.000.000.000 bayt` (Turkish grouping). */
export function formatExactBytes(bytes: number): string {
  return `${bytes.toLocaleString(locale)} bayt`
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
