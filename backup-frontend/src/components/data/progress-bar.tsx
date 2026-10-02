import { cn } from '@/lib/utils'

interface ProgressBarProps {
  value?: number
  indeterminate?: boolean
  className?: string
}

export function ProgressBar({
  value,
  indeterminate,
  className,
}: ProgressBarProps) {
  return (
    <div
      className={cn(
        'h-2 w-full overflow-hidden rounded-full bg-surface-2',
        className
      )}
      role="progressbar"
      aria-valuemin={0}
      aria-valuemax={100}
      aria-valuenow={indeterminate ? undefined : value}
    >
      {indeterminate ? (
        <div className="h-full w-2/3 animate-pulse rounded-full bg-primary" />
      ) : (
        <div
          className="h-full rounded-full bg-primary transition-all"
          style={{ width: `${Math.min(100, Math.max(0, value ?? 0))}%` }}
        />
      )}
    </div>
  )
}
