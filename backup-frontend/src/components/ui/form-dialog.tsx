import { tx, useLanguage } from '@/lib/i18n'
import type { FormEvent, ReactNode } from 'react'
import { Dialog } from '@base-ui/react/dialog'
import { Button } from '@/components/ui/button'
import { DialogBody, DialogFooter, DialogHeader, DialogPopup } from '@/components/ui/dialog-parts'
import { ErrorNotice } from '@/components/ui/notice'
import type { ErrorScope } from '@/lib/api/failure-messages'

/** Mounted only while open, so each opening starts with fresh form state. `error` is the failed request, shown as the three-part error. `destructive` makes the submit button red, for removals the user confirms with a password or code. */
export function FormDialog({ title, description, submitLabel, pendingLabel, pending, error, errorScope, disabled, destructive, onSubmit, onClose, children }: {
  title: string; description?: ReactNode; submitLabel: string; pendingLabel: string; pending: boolean; error?: unknown; errorScope?: ErrorScope
  disabled?: boolean; destructive?: boolean; onSubmit: () => void; onClose: () => void; children: ReactNode
}) {
  useLanguage()
  function submit(event: FormEvent) { event.preventDefault(); if (!pending && !disabled) onSubmit() }
  return <Dialog.Root open onOpenChange={(next) => { if (!next && !pending) onClose() }} disablePointerDismissal={pending}>
    <Dialog.Portal>
      <Dialog.Backdrop className="fixed inset-0 z-40 bg-scrim" />
      <DialogPopup size="lg">
        <DialogHeader title={title} description={description} />
        <form onSubmit={submit} className="contents">
          <DialogBody>
            {children}
            {error ? <ErrorNotice error={error} scope={errorScope} /> : null}
          </DialogBody>
          <DialogFooter>
            <Dialog.Close render={<Button type="button" variant="outline" disabled={pending} />}>{tx("Vazgeç")}</Dialog.Close>
            <Button type="submit" variant={destructive ? 'destructive' : undefined} disabled={pending || disabled}>{pending ? pendingLabel : submitLabel}</Button>
          </DialogFooter>
        </form>
      </DialogPopup>
    </Dialog.Portal>
  </Dialog.Root>
}
