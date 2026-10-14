/**
 * Decimal units only; bits and bytes are never mixed: 1 Mbit/s = 125,000 B/s, 1 MB/s = 1,000,000 B/s. The API unit is always bytes per second.
 * Order matters: `formatBandwidth` / `toInput` pick the first unit that shows the value as a whole number.
 */
export const bandwidthUnits = [
  { id: 'Mbit/s', bytesPerSecond: 125_000 },
  { id: 'MB/s', bytesPerSecond: 1_000_000 },
  { id: 'KB/s', bytesPerSecond: 1_000 },
  { id: 'B/s', bytesPerSecond: 1 },
] as const
export type BandwidthUnitId = (typeof bandwidthUnits)[number]['id']

const locale = 'tr-TR'
const unitSize = (unit: BandwidthUnitId) => bandwidthUnits.find(item => item.id === unit)?.bytesPerSecond ?? 1

/** The first unit (Mbit/s, MB/s, KB/s, B/s) in which `bytesPerSecond` is a whole number; B/s always fits. */
export function cleanUnit(bytesPerSecond: number) {
  return bandwidthUnits.find(unit => bytesPerSecond % unit.bytesPerSecond === 0) ?? bandwidthUnits[bandwidthUnits.length - 1]
}

/** `10 Mbit/s` for 1,250,000 B/s. */
export function formatBandwidth(bytesPerSecond: number): string {
  const unit = cleanUnit(bytesPerSecond)
  return `${(bytesPerSecond / unit.bytesPerSecond).toLocaleString(locale)} ${unit.id}`
}

/** `1.250.000 B/s` (Turkish grouping), shown beside the friendly value so a bits/bytes mix-up is visible. */
export function formatExactRate(bytesPerSecond: number): string {
  return `${bytesPerSecond.toLocaleString(locale)} B/s`
}

/** Editor text that round-trips through `parseBandwidth` (plain digits, no grouping). */
export function toInput(bytesPerSecond: number): { text: string; unit: BandwidthUnitId } {
  const unit = cleanUnit(bytesPerSecond)
  return { text: String(bytesPerSecond / unit.bytesPerSecond), unit: unit.id }
}

export type BandwidthParseResult = { bytesPerSecond: number } | { error: 'invalid' | 'out_of_range' }

/**
 * Exact BigInt arithmetic (no float multiplication of a fraction). Up to 6 decimals; the result must be a whole number of bytes per second,
 * so `1,5 B/s` is invalid rather than silently rounded.
 */
export function parseBandwidth(text: string, unit: BandwidthUnitId, minimum: number, maximum: number): BandwidthParseResult {
  const match = /^(\d{1,15})(?:[.,](\d{1,6}))?$/.exec(text.trim())
  if (!match) return { error: 'invalid' }
  const decimals = match[2] ?? ''
  const scaled = BigInt(match[1] + decimals) * BigInt(unitSize(unit))
  const divisor = 10n ** BigInt(decimals.length)
  if (scaled % divisor !== 0n) return { error: 'invalid' }
  const bytes = scaled / divisor
  if (bytes < BigInt(minimum) || bytes > BigInt(maximum)) return { error: 'out_of_range' }
  return { bytesPerSecond: Number(bytes) }
}

export function bandwidthErrorMessage(error: 'invalid' | 'out_of_range', minimum: number, maximum: number) {
  return error === 'invalid'
    ? 'Değeri sayı olarak girin (en fazla 6 ondalık basamak); sonuç tam bayt/sn olmalıdır.'
    : `Hız ${formatBandwidth(minimum)} (${formatExactRate(minimum)}) ile ${formatBandwidth(maximum)} (${formatExactRate(maximum)}) arasında olmalıdır.`
}
