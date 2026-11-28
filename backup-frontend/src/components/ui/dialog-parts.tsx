import type { ComponentProps, KeyboardEvent, ReactNode } from 'react'
import { Dialog } from '@base-ui/react/dialog'
import { cn } from '@/lib/utils'

const focusable = 'a[href],button:not([disabled]),input:not([disabled]):not([type=hidden]),select:not([disabled]),textarea:not([disabled]),summary,[tabindex]:not([tabindex="-1"])'

const isRadio = (element: Element): element is HTMLInputElement => element instanceof HTMLInputElement && element.type === 'radio'

function tabbable(popup: HTMLElement) {
  const found = [...popup.querySelectorAll<HTMLElement>(focusable)].filter(element => !element.matches(':disabled') && (element.checkVisibility?.() ?? true))
  // A radio group is one tab stop: the checked button, or the first when none is checked.
  return found.filter(element => {
    if (!isRadio(element) || !element.name) return true
    const group = found.filter(other => isRadio(other) && other.name === element.name) as HTMLInputElement[]
    return (group.find(other => other.checked) ?? group[0]) === element
  })
}

/**
 * Base UI redirects a Tab that leaves the popup one animation frame later, so a quick second Tab slips behind the scrim.
 * Wrapping synchronously at both ends keeps focus inside the dialog on every keypress.
 */
function keepTabInside(event: KeyboardEvent<HTMLElement>) {
  if (event.key !== 'Tab' || event.defaultPrevented) return
  const items = tabbable(event.currentTarget)
  const first = items[0], last = items[items.length - 1]
  if (!first || !last) { event.preventDefault(); return }
  const active = document.activeElement
  if (event.shiftKey && (active === first || active === event.currentTarget)) { event.preventDefault(); last.focus() }
  else if (!event.shiftKey && active === last) { event.preventDefault(); first.focus() }
}

const sizes = { md: 'max-w-md', lg: 'max-w-lg', xl: 'max-w-2xl' } as const

/** The one modal shell: centered, never taller than the screen, focus wrapped inside. Compose it with `DialogBody` (scrolls) and `DialogFooter` (always visible). */
export function DialogPopup({ size = 'md', className, onKeyDown, children, ...props }: { size?: keyof typeof sizes; className?: string } & Omit<ComponentProps<typeof Dialog.Popup>, 'className'>) {
  return <Dialog.Popup {...props} onKeyDown={event => { onKeyDown?.(event); keepTabInside(event) }}
    className={cn('fixed left-1/2 top-1/2 z-50 flex max-h-[calc(100dvh-2rem)] w-[calc(100%-2rem)] -translate-x-1/2 -translate-y-1/2 flex-col overflow-hidden rounded-lg border border-border bg-surface-raised shadow-overlay', sizes[size], className)}>{children}</Dialog.Popup>
}

export function DialogHeader({ title, description }: { title: ReactNode; description?: ReactNode }) {
  return <div className="shrink-0 space-y-1 px-6 pb-2 pt-6">
    <Dialog.Title className="text-lg font-semibold text-text-heading">{title}</Dialog.Title>
    {description && <Dialog.Description className="text-sm text-text-muted">{description}</Dialog.Description>}
  </div>
}

export function DialogBody({ className, children }: { className?: string; children: ReactNode }) {
  return <div className={cn('min-h-0 flex-1 space-y-4 overflow-y-auto px-6 py-2', className)}>{children}</div>
}

/** Outside the scrolling body, so Cancel and the primary action stay reachable in short windows. */
export function DialogFooter({ className, children }: { className?: string; children: ReactNode }) {
  return <div className={cn('flex shrink-0 flex-wrap justify-end gap-2 border-t border-border px-6 py-4', className)}>{children}</div>
}
