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
