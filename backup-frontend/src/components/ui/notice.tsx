import type { ReactNode } from 'react'
import { AlertTriangle, CheckCircle2, Info, OctagonAlert } from 'lucide-react'
import { tx, useLanguage } from '@/lib/i18n'
import { type ErrorDescription, type ErrorScope, actionLabel, describeError, describeFailure } from '@/lib/api/failure-messages'
import { Button } from '@/components/ui/button'
import { TechnicalDetails } from '@/components/ui/technical-details'
import { cn } from '@/lib/utils'

const tones = {
  info: { icon: Info, box: 'border-border bg-neutral-subtle', text: 'text-neutral' },
  success: { icon: CheckCircle2, box: 'border-success-border bg-success-subtle', text: 'text-success' },
  warning: { icon: AlertTriangle, box: 'border-warning-border bg-warning-subtle', text: 'text-warning' },
  error: { icon: OctagonAlert, box: 'border-error-border bg-error-subtle', text: 'text-error' },
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

type ErrorNoticeProps = {
  title?: ReactNode
  /** Reloads or repeats what failed: the button of the retry class, and the fallback when a class action has no `onAction`. */
  onRetry?: () => void
  /** What the class action of this failure does (check the computer, fix the details, manage storage, back up now); the screen decides where it leads. */
  onAction?: () => void
  /** Replaces the action button when the one action is a link or another control. */
  control?: ReactNode
  className?: string
} & (
  | { error: unknown; scope?: ErrorScope }
  | { failureCode: string | null | undefined; scope?: ErrorScope }
  | (Omit<ErrorDescription, 'technical'> & Partial<Pick<ErrorDescription, 'technical'>>)
)

/**
 * The one way to show a failure: what happened, what it means for the data, and what to do (one action, as a button). The raw code lives in the
 * collapsed technical details, except for a code the UI has no wording for, which stays open so it can be copied for support.
 * Pass the failure as `error`, or the three parts directly when the failure is not an API error.
 */
export function ErrorNotice(props: ErrorNoticeProps) {
  useLanguage()
  const { what, dataImpact, action, actionKind, unknown, technical = [] } = 'error' in props ? describeError(props.error, props.scope) : 'failureCode' in props ? describeFailure(props.failureCode, props.scope) : props
  const { icon: Icon, box, text } = tones.error
  const run = actionKind === 'retry' ? props.onRetry ?? props.onAction : actionKind ? props.onAction ?? props.onRetry : props.onRetry
  const label = actionKind && (actionKind === 'retry' || props.onAction) ? actionLabel(actionKind) : actionLabel('retry')
  return <div role="alert" className={cn('flex gap-3 rounded-lg border px-4 py-3', box, props.className)}>
    <Icon aria-hidden="true" className={cn('mt-0.5 size-5 shrink-0', text)} />
    <div className="min-w-0 flex-1 space-y-2">
      {props.title && <div className={cn('font-medium', text)}>{props.title}</div>}
      <p className="text-sm font-medium text-text">{what}</p>
      <p className="text-sm text-text"><span className="font-medium">{tx("Verileriniz için:")}</span>{' '}{dataImpact}</p>
      <p className="text-sm text-text"><span className="font-medium">{tx("Ne yapmalı:")}</span>{' '}{action}</p>
      <TechnicalDetails items={technical} accordion={unknown} defaultOpen={unknown} />
    </div>
    {(props.control || run) && <div className="shrink-0 self-center">{props.control ?? <Button type="button" variant="outline" onClick={run}>{label}</Button>}</div>}
  </div>
}
