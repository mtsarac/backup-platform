import { tx, useLanguage } from '@/lib/i18n'
import type { FormEvent, ReactNode } from 'react'
import { Dialog } from '@base-ui/react/dialog'
import { Button } from '@/components/ui/button'

/** Mounted only while open, so each opening starts with fresh form state. */
export function FormDialog({ title, description, submitLabel, pendingLabel, pending, error, disabled, onSubmit, onClose, children }: {
  title: string; description?: ReactNode; submitLabel: string; pendingLabel: string; pending: boolean; error?: string
  disabled?: boolean; onSubmit: () => void; onClose: () => void; children: ReactNode
}) {
  useLanguage()
  function submit(event: FormEvent) { event.preventDefault(); if (!pending && !disabled) onSubmit() }
  return <Dialog.Root open onOpenChange={(next) => { if (!next && !pending) onClose() }} disablePointerDismissal={pending}>
    <Dialog.Portal>
      <Dialog.Backdrop className="fixed inset-0 z-40 bg-black/50" />
      <Dialog.Popup className="fixed left-1/2 top-1/2 z-50 max-h-[90dvh] w-[calc(100%-2rem)] max-w-lg -translate-x-1/2 -translate-y-1/2 space-y-4 overflow-y-auto rounded-xl border border-border bg-surface p-6 shadow-overlay">
        <Dialog.Title className="text-lg font-semibold text-text-heading">{title}</Dialog.Title>
        {description && <Dialog.Description className="text-sm text-text-muted">{description}</Dialog.Description>}
        <form onSubmit={submit} className="space-y-4">
          {children}
          {error && <p role="alert" className="text-sm text-error">{tx(error)}</p>}
          <div className="flex flex-wrap justify-end gap-2">
            <Dialog.Close render={<Button type="button" variant="outline" disabled={pending} />}>{tx("Vazgeç")}</Dialog.Close>
            <Button type="submit" disabled={pending || disabled}>{pending ? pendingLabel : submitLabel}</Button>
          </div>
        </form>
      </Dialog.Popup>
    </Dialog.Portal>
  </Dialog.Root>
}
