/** Product rule: 1 GB = 1,000,000,000 bytes (decimal, never 1024-based). Bytes stay the API unit. */
export const BYTES_PER_GB = 1_000_000_000
export const MAX_STORAGE_BYTES = 1_000_000_000_000_000
const BYTES_PER_MILLI_GB = 1_000_000
const MAX_WHOLE_GB = MAX_STORAGE_BYTES / BYTES_PER_GB

const locale = 'tr-TR'

/** Exact decimal digits after the comma (no rounding), or '' for whole GB. */
function fraction(bytes: number) {
  return String(bytes % BYTES_PER_GB).padStart(9, '0').replace(/0+$/, '')
}

function wholeGb(bytes: number) {
  return (bytes - (bytes % BYTES_PER_GB)) / BYTES_PER_GB
}

/** `5 GB`, `2,5 GB`, `Limitsiz` for null. */
export function formatGb(bytes: number | null): string {
  if (bytes === null) return 'Limitsiz'
  const digits = fraction(bytes)
  return `${wholeGb(bytes).toLocaleString(locale)}${digits ? `,${digits}` : ''} GB`
}

/** `5.000.000.000 bayt` (Turkish grouping). */
export function formatBytes(bytes: number): string {
  return `${bytes.toLocaleString(locale)} bayt`
}

/** Text for a GB input field that round-trips through `parseGb` (up to 3 decimals). */
export function toGbInput(bytes: number): string {
  const digits = fraction(bytes)
  return `${wholeGb(bytes)}${digits ? `,${digits}` : ''}`
}

export type GbParseResult = { bytes: number } | { error: 'invalid' | 'out_of_range' }

/** Exact integer arithmetic only: whole GB and milli-GB are combined as integers, never via float multiplication of a fraction. */
export function parseGb(text: string): GbParseResult {
  const match = /^(\d{1,9})(?:[.,](\d{1,3}))?$/.exec(text.trim())
  if (!match) return { error: 'invalid' }
  const whole = Number(match[1])
  if (whole > MAX_WHOLE_GB) return { error: 'out_of_range' }
  const bytes = whole * BYTES_PER_GB + Number((match[2] ?? '').padEnd(3, '0')) * BYTES_PER_MILLI_GB
  if (bytes <= 0 || bytes > MAX_STORAGE_BYTES) return { error: 'out_of_range' }
  return { bytes }
}

export function gbErrorMessage(error: 'invalid' | 'out_of_range') {
  return error === 'invalid'
    ? 'Boyutu GB cinsinden, en fazla 3 ondalık basamakla girin (örn. 2,5).'
    : 'Boyut 0’dan büyük ve en fazla 1.000.000 GB olmalıdır.'
}
