import { currentLocale, tx } from '@/lib/i18n'
/** Bandwidth is entered and shown in Mbit/s only (1 Mbit/s = 125,000 B/s); the API unit is always bytes per second. */
const BYTES_PER_MBIT = 125_000

/** `10 Mbit/sn` for 1,250,000 B/s. */
export function formatBandwidth(bytesPerSecond: number): string {
  return `${(bytesPerSecond / BYTES_PER_MBIT).toLocaleString(currentLocale(), { maximumFractionDigits: 6 })} ${tx('Mbit/sn')}`
}

/** `1.250.000 B/sn` (Turkish grouping): the exact value for technical details, never the headline. */
export function formatExactRate(bytesPerSecond: number): string {
  return `${bytesPerSecond.toLocaleString(currentLocale())} ${tx('B/sn')}`
}

/** Editor text that round-trips through `parseBandwidth` (plain digits, no grouping). */
export const toInput = (bytesPerSecond: number) => String(bytesPerSecond / BYTES_PER_MBIT)

export type BandwidthParseResult = { bytesPerSecond: number } | { error: 'invalid' | 'out_of_range' }

/**
 * Mbit/s text to bytes per second. Exact BigInt arithmetic (no float multiplication of a fraction). Up to 6 decimals; the result must be a whole number of bytes per second,
 * so a value that is not is invalid rather than silently rounded.
 */
export function parseBandwidth(text: string, minimum: number, maximum: number): BandwidthParseResult {
  const match = /^(\d{1,15})(?:[.,](\d{1,6}))?$/.exec(text.trim())
  if (!match) return { error: 'invalid' }
  const decimals = match[2] ?? ''
  const scaled = BigInt(match[1] + decimals) * BigInt(BYTES_PER_MBIT)
  const divisor = 10n ** BigInt(decimals.length)
  if (scaled % divisor !== 0n) return { error: 'invalid' }
  const bytes = scaled / divisor
  if (bytes < BigInt(minimum) || bytes > BigInt(maximum)) return { error: 'out_of_range' }
  return { bytesPerSecond: Number(bytes) }
}

export function bandwidthErrorMessage(error: 'invalid' | 'out_of_range', minimum: number, maximum: number) {
  return error === 'invalid'
    ? tx('Değeri sayı olarak girin (en fazla 6 ondalık basamak); sonuç tam bayt/sn olmalıdır.')
    : tx("Hız {0} ile {1} arasında olmalıdır.", { "0": formatBandwidth(minimum), "1": formatBandwidth(maximum) })
}
