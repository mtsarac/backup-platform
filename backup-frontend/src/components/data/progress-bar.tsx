import { cn } from '@/lib/utils'
import { progressFill } from './progress-tone'


interface ProgressBarProps {
  /** Names the bar for assistive tech; there is no visible text inside it. */
  label: string
  /** Percent 0–100. Missing means "not reported": the bar stays empty and announces no value. */
  value?: number | null
  /** A lighter share drawn after `value` (quota reservations). */
  reserved?: number
  /** What assistive tech announces when it differs from the drawn `value`. */
  now?: number
  valueText?: string
  tone?: keyof typeof progressFill
  className?: string
}

const clamp = (percent: number) => Math.min(100, Math.max(0, percent))

/** The one progress bar: restore and backup attempts, storage capacity and quota use. */
export function ProgressBar({ label, value, reserved, now, valueText, tone = 'primary', className }: ProgressBarProps) {
  const announced = now ?? value ?? undefined
  return (
    <div
      role="progressbar"
      aria-label={label}
      aria-valuemin={0}
      aria-valuemax={100}
      aria-valuenow={announced}
      aria-valuetext={valueText}
      className={cn('flex h-2 w-full overflow-hidden rounded-full bg-surface-2', className)}
    >
      {value != null && <div className={cn('h-full transition-all', progressFill[tone])} style={{ width: `${clamp(value)}%` }} />}
      {reserved != null && <div className={cn('h-full opacity-45 transition-all', progressFill[tone])} style={{ width: `${clamp(reserved)}%` }} />}
    </div>
  )
}
