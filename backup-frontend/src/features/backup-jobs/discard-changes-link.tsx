import { tx, useLanguage } from '@/lib/i18n'
import { useEffect } from 'react'
import { Link, useBlocker } from 'react-router-dom'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'

function useLeaveGuard(dirty: boolean) {
  const blocker = useBlocker(dirty)
  useEffect(() => {
    if (!dirty) return
    const warn = (event: BeforeUnloadEvent) => {
      event.preventDefault()
      event.returnValue = ''
    }
    window.addEventListener('beforeunload', warn)
    return () => window.removeEventListener('beforeunload', warn)
  }, [dirty])
  return blocker
}

/** Confirms dropping unsaved edits; `title` is what the dialog asks. */
export function DiscardDialog({ open, title, onClose, onDiscard }: { open: boolean; title: string; onClose: () => void; onDiscard: () => void }) {
  useLanguage()
  return <ConfirmDialog open={open} onClose={onClose} pending={false} title={title}
    consequence={{ what: tx("Bu formda girdiğiniz değerler kaydedilmeden kapatılır."), who: tx("Yalnızca bu formu etkiler; kayıtlı işler ve yedekler değişmez."), reversible: tx("Hayır, değerleri yeniden girmeniz gerekir.") }}
    confirmLabel={tx("Değişiklikleri at")} onConfirm={onDiscard} />
}

/** Protects this form from router navigation and from reloading or closing the tab. */
export function DiscardChangesLink({ to, dirty }: { to: string; dirty: boolean }) {
  useLanguage()
  const blocker = useLeaveGuard(dirty)
  return <>
    <Link className="text-sm text-primary underline-offset-4 hover:underline" to={to}>{tx("Vazgeç")}</Link>
    <DiscardDialog open={blocker.state === 'blocked'} title={tx("Kaydedilmemiş değişiklikler atılsın mı?")} onClose={() => blocker.reset?.()} onDiscard={() => blocker.proceed?.()} />
  </>
}

/** The same protection for a form that has its own Vazgeç button. */
export function UnsavedChangesGuard({ dirty }: { dirty: boolean }) {
  const blocker = useLeaveGuard(dirty)
  return <DiscardDialog open={blocker.state === 'blocked'} title={tx("Değişiklikler kaydedilmedi")} onClose={() => blocker.reset?.()} onDiscard={() => blocker.proceed?.()} />
}
