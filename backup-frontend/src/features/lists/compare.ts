import { currentLocale } from '@/lib/i18n'

/**
 * Ascending comparators for `useClientList`. A missing value (null/undefined/NaN) goes last in either direction:
 * the comparator answers ±Infinity for it, which the list does not flip when the order is descending.
 */
const nullable = <T>(a: T | null | undefined, b: T | null | undefined, order: (a: T, b: T) => number) => {
  const none = (value: T | null | undefined) => value === null || value === undefined || (typeof value === 'number' && Number.isNaN(value))
  return none(a) || none(b) ? (none(a) ? (none(b) ? 0 : Infinity) : -Infinity) : order(a as T, b as T)
}

export const compareText = (a?: string | null, b?: string | null) => nullable(a, b, (x, y) => x.localeCompare(y, currentLocale()))
export const compareNumber = (a?: number | null, b?: number | null) => nullable(a, b, (x, y) => x - y)
/** ISO date strings (or dates). */
export const compareDate = (a?: string | Date | null, b?: string | Date | null) => compareNumber(a ? new Date(a).getTime() : null, b ? new Date(b).getTime() : null)
