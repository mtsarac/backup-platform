import { Badge } from '@/components/ui/badge'

export type CompatibilityStatus = 'compatible' | 'warning' | 'unsupported'

interface CompatibilityIndicatorProps {
  status: CompatibilityStatus
  message: string
}

function tone(status: CompatibilityStatus) {
  switch (status) {
    case 'compatible':
      return 'success'
    case 'warning':
      return 'warning'
    case 'unsupported':
      return 'error'
    default:
      return assertNever(status)
  }
}

function label(status: CompatibilityStatus): string {
  switch (status) {
    case 'compatible':
      return 'Uyumlu'
    case 'warning':
      return 'Uyarı'
    case 'unsupported':
      return 'Desteklenmiyor'
    default:
      return assertNever(status)
  }
}

function assertNever(value: never): never {
  throw new Error(`Unexpected compatibility status: ${String(value)}`)
}

export function CompatibilityIndicator({ status, message }: CompatibilityIndicatorProps) {
  return (
    <div className="rounded-lg border border-border bg-surface p-4">
      <div className="mb-2 flex items-center justify-between gap-3">
        <h2 className="text-sm font-medium text-text-heading">Uyumluluk</h2>
        <Badge tone={tone(status)}>{label(status)}</Badge>
      </div>
      <p className="text-sm text-text-muted">{message}</p>
    </div>
  )
}
