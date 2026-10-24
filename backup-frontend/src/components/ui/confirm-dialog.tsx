import { tx, useLanguage } from '@/lib/i18n'
import { useId, useRef, useState, type FormEvent, type ReactNode } from 'react'
import { Dialog } from '@base-ui/react/dialog'
import { Button } from '@/components/ui/button'
import { ErrorNotice } from '@/components/ui/notice'
import type { ErrorDescription, ErrorScope } from '@/lib/api/failure-messages'

/** The plain template every risky action follows: what happens, who is affected, whether it can be undone. */
export interface Consequence { what: ReactNode; who: ReactNode; reversible: ReactNode }

interface ConfirmProps {
  open: boolean
  onClose: () => void
  title: string
  consequence: Consequence
  pending: boolean
  /** The failed request, or an already-described failure; shown as the three-part error. */
  error?: unknown
  errorScope?: ErrorScope
  onConfirm: () => void
  confirmLabel: string
  confirmDisabled?: boolean
  /** Inputs the action needs (for example a value to set), shown under the consequences. */
  children?: ReactNode
  /** When set, the action stays blocked until the user types this name exactly; for deletions and other hard-to-undo actions. */
  name?: string
}

const isDescribed = (error: unknown): error is ErrorDescription => typeof error === 'object' && error !== null && 'dataImpact' in error

function Consequences({ consequence }: { consequence: Consequence }) {
  return <dl className="space-y-2 text-sm">
    {([[tx("Ne olacak?"), consequence.what], [tx("Kimleri etkiler?"), consequence.who], [tx("Geri alınabilir mi?"), consequence.reversible]] as const).map(([label, text]) => <div key={label}>
      <dt className="font-medium text-text-heading">{label}</dt>
      <dd className="text-text-muted">{text}</dd>
    </div>)}
  </dl>
}

/** The typed name lives inside the popup, so it is forgotten whenever the dialog closes. */
function Body({ title, consequence, pending, error, errorScope, onConfirm, confirmLabel, confirmDisabled, children, name }: Omit<ConfirmProps, 'open' | 'onClose'>) {
  const cancelRef = useRef<HTMLButtonElement>(null)
  const inputId = useId()
  const [typed, setTyped] = useState('')
  const blocked = pending || confirmDisabled || (name !== undefined && typed !== name)
  function submit(event: FormEvent) { event.preventDefault(); if (!blocked) onConfirm() }
  return <Dialog.Popup initialFocus={name === undefined ? cancelRef : undefined} className="fixed left-1/2 top-1/2 z-50 max-h-[90dvh] w-[calc(100%-2rem)] max-w-md -translate-x-1/2 -translate-y-1/2 space-y-4 overflow-y-auto rounded-xl border border-border bg-surface p-6 shadow-overlay">
    <Dialog.Title className="text-lg font-semibold text-text-heading">{title}</Dialog.Title>
    <Dialog.Description render={<div />} className="text-sm"><Consequences consequence={consequence} /></Dialog.Description>
    <form onSubmit={submit} className="space-y-4">
      {children}
      {name !== undefined && <div className="space-y-1">
        <label htmlFor={inputId} className="block text-sm font-medium text-text-heading">{tx("Onaylamak için adı yazın: {0}", { "0": name })}</label>
        <input id={inputId} value={typed} onChange={event => setTyped(event.target.value)} disabled={pending} autoComplete="off" spellCheck={false} className="block min-h-11 w-full rounded-lg border border-border-strong/70 bg-surface px-3 py-2 font-mono text-text shadow-card disabled:opacity-60" />
      </div>}
      {error ? isDescribed(error) ? <ErrorNotice {...error} /> : <ErrorNotice error={error} scope={errorScope} /> : null}
      <div className="flex flex-wrap justify-end gap-2">
        <Dialog.Close render={<Button ref={cancelRef} type="button" variant="outline" disabled={pending} />}>{tx("Vazgeç")}</Dialog.Close>
        <Button type="submit" disabled={blocked}>{pending ? tx('İşleniyor…') : confirmLabel}</Button>
      </div>
    </form>
  </Dialog.Popup>
}

/** Consequence dialog for risky actions; pass `name` to also require typing it. */
export function ConfirmDialog({ open, onClose, pending, ...body }: ConfirmProps) {
  useLanguage()
  return <Dialog.Root open={open} onOpenChange={next => { if (!next && !pending) onClose() }} disablePointerDismissal={pending}>
    <Dialog.Portal>
      <Dialog.Backdrop className="fixed inset-0 z-40 bg-black/50" />
      <Body pending={pending} {...body} />
    </Dialog.Portal>
  </Dialog.Root>
}
