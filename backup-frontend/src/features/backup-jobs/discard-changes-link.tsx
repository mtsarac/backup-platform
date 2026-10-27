import { tx, useLanguage } from '@/lib/i18n'
import { useEffect, useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'

/**
 * "Vazgeç" for a form: asks before throwing away entered data, and makes the browser warn on reload or tab close.
 * shortcut: the app uses BrowserRouter, so other in-app links (sidebar) and the back button are not intercepted; switch to a data router and `useBlocker` to cover them.
 */
export function DiscardChangesLink({ to, dirty }: { to: string; dirty: boolean }) {
  useLanguage()
  const navigate = useNavigate()
  const [asking, setAsking] = useState(false)
  useEffect(() => {
    if (!dirty) return
    const warn = (event: BeforeUnloadEvent) => event.preventDefault()
    window.addEventListener('beforeunload', warn)
    return () => window.removeEventListener('beforeunload', warn)
  }, [dirty])
  return <>
    <Link className="text-sm text-primary underline-offset-4 hover:underline" to={to} onClick={event => { if (dirty) { event.preventDefault(); setAsking(true) } }}>{tx("Vazgeç")}</Link>
    <ConfirmDialog open={asking} onClose={() => setAsking(false)} pending={false} title={tx("Kaydedilmemiş değişiklikler atılsın mı?")}
      consequence={{ what: tx("Bu formda girdiğiniz değerler kaydedilmeden kapatılır."), who: tx("Yalnızca bu formu etkiler; kayıtlı işler ve yedekler değişmez."), reversible: tx("Hayır, değerleri yeniden girmeniz gerekir.") }}
      confirmLabel={tx("Değişiklikleri at")} onConfirm={() => navigate(to)} />
  </>
}
