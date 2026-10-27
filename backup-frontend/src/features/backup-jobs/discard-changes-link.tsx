import { tx, useLanguage } from '@/lib/i18n'
import { useEffect } from 'react'
import { Link, useBlocker } from 'react-router-dom'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'

/** Protects this form from router navigation and from reloading or closing the tab. */
export function DiscardChangesLink({ to, dirty }: { to: string; dirty: boolean }) {
  useLanguage()
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
  return <>
    <Link className="text-sm text-primary underline-offset-4 hover:underline" to={to}>{tx("Vazgeç")}</Link>
    <ConfirmDialog open={blocker.state === 'blocked'} onClose={() => blocker.reset?.()} pending={false} title={tx("Kaydedilmemiş değişiklikler atılsın mı?")}
      consequence={{ what: tx("Bu formda girdiğiniz değerler kaydedilmeden kapatılır."), who: tx("Yalnızca bu formu etkiler; kayıtlı işler ve yedekler değişmez."), reversible: tx("Hayır, değerleri yeniden girmeniz gerekir.") }}
      confirmLabel={tx("Değişiklikleri at")} onConfirm={() => blocker.proceed?.()} />
  </>
}
