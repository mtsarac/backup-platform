import { useRef, type ReactNode } from 'react'
import { Dialog } from '@base-ui/react/dialog'
import { Button } from '@/components/ui/button'

export function ConfirmDialog({ open, onClose, title, description, pending, error, onConfirm, confirmLabel = 'İptali onayla', confirmDisabled = false }: {
  open: boolean; onClose: () => void; title: string; description: ReactNode;
  pending: boolean; error?: string; onConfirm: () => void; confirmLabel?: string; confirmDisabled?: boolean
}) {
  const cancelRef = useRef<HTMLButtonElement>(null)
  return <Dialog.Root open={open} onOpenChange={(next) => { if (!next && !pending) onClose() }} disablePointerDismissal={pending}>
    <Dialog.Portal>
      <Dialog.Backdrop className="fixed inset-0 z-40 bg-black/50" />
      <Dialog.Popup initialFocus={cancelRef} className="fixed left-1/2 top-1/2 z-50 max-h-[90dvh] w-[calc(100%-2rem)] max-w-md -translate-x-1/2 -translate-y-1/2 space-y-4 overflow-y-auto rounded-xl border border-border bg-surface p-6 shadow-overlay">
        <Dialog.Title className="text-lg font-semibold text-text-heading">{title}</Dialog.Title>
        <Dialog.Description className="text-sm text-text-muted">{description}</Dialog.Description>
        {error && <p role="alert" className="text-sm text-error">{error}</p>}
        <div className="flex flex-wrap justify-end gap-2">
          <Dialog.Close render={<Button ref={cancelRef} variant="outline" disabled={pending} />}>Vazgeç</Dialog.Close>
          <Button disabled={pending || confirmDisabled} onClick={onConfirm}>{pending ? 'İşleniyor…' : confirmLabel}</Button>
        </div>
      </Dialog.Popup>
    </Dialog.Portal>
  </Dialog.Root>
}
