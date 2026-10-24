import type { ReactNode } from 'react'
import { AlertTriangle, CheckCircle2, Info, OctagonAlert } from 'lucide-react'
import { tx, useLanguage } from '@/lib/i18n'
import { type ErrorDescription, type ErrorScope, describeError, describeFailure } from '@/lib/api/failure-messages'
import { Button } from '@/components/ui/button'
import { TechnicalDetails } from '@/components/ui/technical-details'
import { cn } from '@/lib/utils'

const tones = {
  info: { icon: Info, box: 'border-info/30 bg-info-subtle', text: 'text-info' },
  success: { icon: CheckCircle2, box: 'border-success/30 bg-success-subtle', text: 'text-success' },
  warning: { icon: AlertTriangle, box: 'border-warning/35 bg-warning-subtle', text: 'text-warning' },
  error: { icon: OctagonAlert, box: 'border-error/35 bg-error-subtle', text: 'text-error' },
} as const

/** An inline message that is not a failure. Failures go through `ErrorNotice`, which cannot omit what the user needs to know. */
export function Notice({ tone = 'info', title, children, action, className }: {
  tone?: Exclude<keyof typeof tones, 'error'>; title?: ReactNode; children?: ReactNode; action?: ReactNode; className?: string
}) {
  const { icon: Icon, box, text } = tones[tone]
  return <div role="status" className={cn('flex gap-3 rounded-lg border px-4 py-3', box, className)}>
    <Icon aria-hidden="true" className={cn('mt-0.5 size-5 shrink-0', text)} />
    <div className="min-w-0 flex-1 space-y-0.5">
      {title && <p className={cn('font-medium', text)}>{title}</p>}
      {children && <div className="text-sm text-text">{children}</div>}
    </div>
    {action && <div className="shrink-0 self-center">{action}</div>}
  </div>
}

type ErrorNoticeProps = { title?: ReactNode; onRetry?: () => void; /** Replaces the retry button when the one action is a link or another control. */ control?: ReactNode; className?: string } & (
  | { error: unknown; scope?: ErrorScope }
  | { failureCode: string | null | undefined; scope?: ErrorScope }
  | (Omit<ErrorDescription, 'technical'> & Partial<Pick<ErrorDescription, 'technical'>>)
)

/**
 * The one way to show a failure: what happened, what it means for the data, and what to do (one action). The raw code lives in the
 * collapsed technical details. Pass the failure as `error`, or the three parts directly when the failure is not an API error.
 */
export function ErrorNotice(props: ErrorNoticeProps) {
  useLanguage()
  const { what, dataImpact, action, technical = [] } = 'error' in props ? describeError(props.error, props.scope) : 'failureCode' in props ? describeFailure(props.failureCode, props.scope) : props
  const { icon: Icon, box, text } = tones.error
  return <div role="alert" className={cn('flex gap-3 rounded-lg border px-4 py-3', box, props.className)}>
    <Icon aria-hidden="true" className={cn('mt-0.5 size-5 shrink-0', text)} />
    <div className="min-w-0 flex-1 space-y-2">
      {props.title && <p className={cn('font-medium', text)}>{props.title}</p>}
      <p className="text-sm font-medium text-text">{what}</p>
      <p className="text-sm text-text"><span className="font-medium">{tx("Verileriniz için:")}</span>{' '}{dataImpact}</p>
      <p className="text-sm text-text"><span className="font-medium">{tx("Ne yapmalı:")}</span>{' '}{action}</p>
      <TechnicalDetails items={technical} />
    </div>
    {(props.control || props.onRetry) && <div className="shrink-0 self-center">{props.control ?? <Button type="button" variant="outline" onClick={props.onRetry}>{tx("Tekrar dene")}</Button>}</div>}
  </div>
}
