import type { ReactNode } from 'react'
import { AlertTriangle, CheckCircle2, Info, OctagonAlert } from 'lucide-react'
import { cn } from '@/lib/utils'

const tones = {
  info: { icon: Info, box: 'border-info/30 bg-info-subtle', text: 'text-info' },
  success: { icon: CheckCircle2, box: 'border-success/30 bg-success-subtle', text: 'text-success' },
  warning: { icon: AlertTriangle, box: 'border-warning/35 bg-warning-subtle', text: 'text-warning' },
  error: { icon: OctagonAlert, box: 'border-error/35 bg-error-subtle', text: 'text-error' },
} as const

/** An inline message. Errors announce as alerts; everything else as a polite status. */
export function Notice({ tone = 'info', title, children, action, className }: {
  tone?: keyof typeof tones; title?: ReactNode; children?: ReactNode; action?: ReactNode; className?: string
}) {
  const { icon: Icon, box, text } = tones[tone]
  return <div role={tone === 'error' ? 'alert' : 'status'} className={cn('flex gap-3 rounded-lg border px-4 py-3', box, className)}>
    <Icon aria-hidden="true" className={cn('mt-0.5 size-5 shrink-0', text)} />
    <div className="min-w-0 flex-1 space-y-0.5">
      {title && <p className={cn('font-medium', text)}>{title}</p>}
      {children && <div className="text-sm text-text">{children}</div>}
    </div>
    {action && <div className="shrink-0 self-center">{action}</div>}
  </div>
}
