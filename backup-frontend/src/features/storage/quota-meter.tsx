import { tx, useLanguage } from '@/lib/i18n'
import { cn } from '@/lib/utils'
import { allocatedPercent } from './format'
import type { QuotaState } from './types'
import { formatBytes } from '@/lib/format'

const fillByState: Record<QuotaState, string> = {
  Normal: 'bg-success',
  Warning: 'bg-warning',
  Critical: 'bg-error',
  OverQuota: 'bg-error',
  Unlimited: 'bg-primary',
}

/**
 * Real used + reserved bytes against the limit, as two segments. The colour is the backend's `state`; nothing here decides what is
 * a warning. With no limit there is nothing to fill against, so the bar is not drawn and the figures stand alone.
 */
export function QuotaMeter({ usedBytes, reservedBytes, limitBytes, state, label = tx('Depolama kullanımı') }: {
  usedBytes: number; reservedBytes: number; limitBytes: number | null; state: QuotaState; label?: string
}) {
  useLanguage()
  const percent = allocatedPercent(usedBytes, reservedBytes, limitBytes)
  if (limitBytes === null || limitBytes <= 0 || percent === null) {
    return <p className="text-sm text-text-muted">{tx("Limit tanımlı değil; kullanım izleniyor ancak doluluk oranı hesaplanamaz.")}</p>
  }
  const committed = Math.max(0, usedBytes) + Math.max(0, reservedBytes)
  // Segment widths are shares of the limit; once committed passes the limit they share the full bar in proportion.
  const scale = Math.max(limitBytes, committed)
  const usedWidth = (Math.max(0, usedBytes) / scale) * 100
  const reservedWidth = (Math.max(0, reservedBytes) / scale) * 100
  const fill = fillByState[state]
  return <div className="space-y-1.5">
    <div role="progressbar" aria-label={label} aria-valuemin={0} aria-valuemax={100} aria-valuenow={percent}
      aria-valuetext={tx("{0} / {1} ayrılmış (%{2})", { "0": formatBytes(committed), "1": formatBytes(limitBytes), "2": percent })}
      className="flex h-3 w-full overflow-hidden rounded-full bg-surface-2">
      <div className={cn('h-full transition-all', fill)} style={{ width: `${usedWidth}%` }} />
      <div className={cn('h-full opacity-45 transition-all', fill)} style={{ width: `${reservedWidth}%` }} />
    </div>
    <div className="flex flex-wrap gap-x-4 gap-y-1 text-xs text-text-muted">
      <span className="inline-flex items-center gap-1.5"><span className={cn('size-2 rounded-full', fill)} aria-hidden="true" />{tx("Kullanılan")}</span>
      <span className="inline-flex items-center gap-1.5"><span className={cn('size-2 rounded-full opacity-45', fill)} aria-hidden="true" />{tx("Rezerve")}</span>
    </div>
  </div>
}
