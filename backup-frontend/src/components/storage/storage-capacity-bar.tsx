import { tx, useLanguage } from '@/lib/i18n'
import { cn } from '@/lib/utils'

interface StorageCapacityBarProps {
  usedBytes: number
  totalBytes: number
}

function formatTb(bytes: number): string {
  return `${(bytes / 1024 ** 4).toFixed(1)} TB`
}

function barTone(percent: number): string {
  if (percent >= 90) return 'bg-error'
  if (percent >= 75) return 'bg-warning'
  return 'bg-success'
}

export function StorageCapacityBar({ usedBytes, totalBytes }: StorageCapacityBarProps) {
  useLanguage()
  const percent = totalBytes > 0 ? Math.round((usedBytes / totalBytes) * 100) : 0

  return (
    <div className="min-w-40 space-y-1">
      <div className="flex justify-between gap-3 text-xs text-text-muted">
        <span>{formatTb(usedBytes)}{' '}{tx("kullanıldı")}</span>
        <span>{percent}%</span>
      </div>
      <div className="h-2 overflow-hidden rounded-full bg-surface-2">
        <div className={cn('h-full rounded-full', barTone(percent))} style={{ width: `${Math.min(100, percent)}%` }} />
      </div>
      <div className="text-xs text-text-muted">{tx("Toplam")}{' '}{formatTb(totalBytes)}</div>
    </div>
  )
}
