import { formatBytes, formatGb } from './storage'

/** Decimal GB with the exact byte count as secondary text; `null` = unlimited. */
export function StorageLimitText({ bytes }: { bytes: number | null }) {
  return <span className="inline-flex flex-col">
    <span className="font-medium">{formatGb(bytes)}</span>
    {bytes !== null && <span className="text-xs text-text-muted">{formatBytes(bytes)}</span>}
  </span>
}
