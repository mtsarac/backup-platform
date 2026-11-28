import { tx, useLanguage } from '@/lib/i18n'
import { useId, useRef, useState, type FormEvent, type ReactNode } from 'react'
import { Dialog } from '@base-ui/react/dialog'
import { Button } from '@/components/ui/button'
import { DialogBody, DialogFooter, DialogHeader, DialogPopup } from '@/components/ui/dialog-parts'
import { controlClass } from '@/components/ui/form-styles'
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
  /** The action cannot be undone: the confirm button is red like the button that opened the dialog. */
  destructive?: boolean
  /** Inputs the action needs (for example a value to set), shown under the consequences. */
  children?: ReactNode
  /** When set, the action stays blocked until the user types this name exactly; for deletions and other hard-to-undo actions. */
  name?: string
}

const isDescribed = (error: unknown): error is ErrorDescription => typeof error === 'object' && error !== null && 'what' in error

function Consequences({ consequence }: { consequence: Consequence }) {
  return <dl className="space-y-2 text-sm">
    {([[tx("Ne olacak?"), consequence.what], [tx("Kimleri etkiler?"), consequence.who], [tx("Geri alınabilir mi?"), consequence.reversible]] as const).map(([label, text]) => <div key={label}>
      <dt className="font-medium text-text-heading">{label}</dt>
      <dd className="text-text-muted">{text}</dd>
    </div>)}
  </dl>
}

/** The typed name lives inside the popup, so it is forgotten whenever the dialog closes. */
function Body({ title, consequence, pending, error, errorScope, onConfirm, confirmLabel, confirmDisabled, destructive, children, name }: Omit<ConfirmProps, 'open' | 'onClose'>) {
  const cancelRef = useRef<HTMLButtonElement>(null)
  const inputId = useId()
  const [typed, setTyped] = useState('')
  const blocked = pending || confirmDisabled || (name !== undefined && typed !== name)
  function submit(event: FormEvent) { event.preventDefault(); event.stopPropagation(); if (!blocked) onConfirm() }
  return <DialogPopup size="md" initialFocus={name === undefined ? cancelRef : undefined}>
    <DialogHeader title={title} />
    <form onSubmit={submit} className="contents">
      <DialogBody>
        <Dialog.Description render={<div />} className="text-sm"><Consequences consequence={consequence} /></Dialog.Description>
        {children}
        {name !== undefined && <div className="space-y-1">
          <label htmlFor={inputId} className="block text-sm font-medium text-text-heading">{tx("Onaylamak için adı yazın: {0}", { "0": name })}</label>
          <input id={inputId} value={typed} onChange={event => setTyped(event.target.value)} disabled={pending} autoComplete="off" spellCheck={false} className={`${controlClass} w-full font-mono`} />
        </div>}
        {error ? isDescribed(error) ? <ErrorNotice {...error} /> : <ErrorNotice error={error} scope={errorScope} /> : null}
      </DialogBody>
      <DialogFooter>
        <Dialog.Close render={<Button ref={cancelRef} type="button" variant="outline" disabled={pending} />}>{tx("Vazgeç")}</Dialog.Close>
        <Button type="submit" variant={destructive ? 'destructive' : undefined} disabled={blocked}>{pending ? tx('İşleniyor…') : confirmLabel}</Button>
      </DialogFooter>
    </form>
  </DialogPopup>
}

/** Consequence dialog for risky actions; pass `name` to also require typing it. */
export function ConfirmDialog({ open, onClose, pending, ...body }: ConfirmProps) {
  useLanguage()
  return <Dialog.Root open={open} onOpenChange={next => { if (!next && !pending) onClose() }} disablePointerDismissal={pending}>
    <Dialog.Portal>
      <Dialog.Backdrop className="fixed inset-0 z-40 bg-scrim" />
      <Body pending={pending} {...body} />
    </Dialog.Portal>
  </Dialog.Root>
}
