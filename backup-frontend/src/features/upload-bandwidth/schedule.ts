import { tx } from '@/lib/i18n'
import type { TransferScheduleWindowDto } from './types'
import { formatBandwidth } from './units'

/** Monday first, Sunday last; `value` is the wire day (0 = Sunday). */
export const DAYS = [
  { value: 1, label: () => tx('Pazartesi') },
  { value: 2, label: () => tx('Salı') },
  { value: 3, label: () => tx('Çarşamba') },
  { value: 4, label: () => tx('Perşembe') },
  { value: 5, label: () => tx('Cuma') },
  { value: 6, label: () => tx('Cumartesi') },
  { value: 0, label: () => tx('Pazar') },
] as const

function dayLabel(days: ReadonlySet<number>) {
  if (days.size === 7) return tx('Her gün')
  if (days.size === 5 && [1, 2, 3, 4, 5].every(day => days.has(day))) return tx('Hafta içi')
  if (days.size === 2 && days.has(6) && days.has(0)) return tx('Hafta sonu')
  return DAYS.filter(day => days.has(day.value)).map(day => day.label()).join(', ')
}

const clock = (time: string) => time.slice(0, 5)

/** One plain line per distinct hours-and-rate ("Hafta içi 09:00–18:00: 10 Mbit/sn"), earliest hours first. Derived only from the profile's windows. */
export function describeSchedule(windows: readonly TransferScheduleWindowDto[]): string[] {
  const groups = new Map<string, { start: string; end: string; bytesPerSecond: number; days: Set<number> }>()
  for (const window of windows) {
    const key = `${window.startTime}|${window.endTime}|${window.bytesPerSecond}`
    const group = groups.get(key) ?? { start: window.startTime, end: window.endTime, bytesPerSecond: window.bytesPerSecond, days: new Set<number>() }
    group.days.add(window.dayOfWeek)
    groups.set(key, group)
  }
  return [...groups.values()]
    .sort((a, b) => a.start.localeCompare(b.start) || a.end.localeCompare(b.end) || a.bytesPerSecond - b.bytesPerSecond)
    .map(group => `${dayLabel(group.days)} ${clock(group.start)}–${clock(group.end)}: ${group.bytesPerSecond === 0 ? tx('Aktarımı Duraklat') : formatBandwidth(group.bytesPerSecond)}`)
}
