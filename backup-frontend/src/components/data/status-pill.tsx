import type { ReactNode } from 'react'
import { AlertTriangle, CheckCircle2, Circle, Clock, Info, LoaderCircle, OctagonAlert, type LucideIcon } from 'lucide-react'
import { useLanguage } from '@/lib/i18n'
import { levelLabel, statusMeta, type StatusDomain, type StatusLevel } from '@/lib/status'
import { cn } from '@/lib/utils'

const levels: Record<StatusLevel, { icon: LucideIcon; className: string }> = {
  safe: { icon: CheckCircle2, className: 'bg-success-subtle text-success' },
  attention: { icon: AlertTriangle, className: 'bg-warning-subtle text-warning' },
  urgent: { icon: OctagonAlert, className: 'bg-error-subtle text-error' },
  running: { icon: LoaderCircle, className: 'bg-info-subtle text-info' },
  pending: { icon: Clock, className: 'bg-muted text-text-muted' },
  info: { icon: Info, className: 'bg-info-subtle text-info' },
  neutral: { icon: Circle, className: 'bg-muted text-text-muted' },
}

type Props = { className?: string } & (
  | { status: string; domain?: StatusDomain; level?: undefined; children?: undefined }
  | { level: StatusLevel; children?: ReactNode; status?: undefined; domain?: undefined }
)

/**
 * Every status is colour + icon + text, so it reads without colour. Look a status up in the dictionary (`status`, optionally in a
 * `domain`), or give a `level` with the text of a computed state; a bare level says its own word ("Güvende", "Dikkat", "Acil").
 */
export function StatusPill({ status, domain = 'general', level, children, className }: Props) {
  useLanguage()
  const meta = level ? { level, label: undefined } : statusMeta(domain, status)
  const { icon: Icon, className: tone } = levels[meta.level]
  return <span className={cn('inline-flex max-w-full items-center gap-1.5 rounded-md px-2 py-0.5 text-xs font-medium leading-5', tone, className)}>
    <Icon aria-hidden="true" className="size-3.5 shrink-0" />
    {children ?? meta.label ?? levelLabel(meta.level)}
  </span>
}
