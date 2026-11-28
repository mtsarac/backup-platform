import { useRef, useState } from 'react'
import { Dialog } from '@base-ui/react/dialog'
import { Button } from '@/components/ui/button'
import { CheckField } from '@/components/ui/check-field'
import { DialogBody, DialogFooter, DialogHeader, DialogPopup } from '@/components/ui/dialog-parts'
import { Notice } from '@/components/ui/notice'
import { tx, useLanguage } from '@/lib/i18n'

/** Blocking one-time display of recovery codes: only the acknowledgement closes it (no Escape, no backdrop). Mount only while codes are issued. */
export function RecoveryCodesDialog({ codes, onClose }: { codes: string[]; onClose: () => void }) {
  useLanguage()
  const [copied, setCopied] = useState(false)
  const [saved, setSaved] = useState(false)
  const copyRef = useRef<HTMLButtonElement>(null)

  async function copy() {
    try {
      await navigator.clipboard.writeText(codes.join('\n'))
      setCopied(true)
      window.setTimeout(() => setCopied(false), 2000)
    } catch { /* the codes stay selectable on screen */ }
  }

  return <Dialog.Root open onOpenChange={() => undefined} disablePointerDismissal>
    <Dialog.Portal>
      <Dialog.Backdrop className="fixed inset-0 z-40 bg-scrim" />
      <DialogPopup initialFocus={copyRef}>
        <DialogHeader title={tx('Kurtarma kodlarınız')} />
        <DialogBody>
          <Notice tone="warning">
            {tx('Bu kodlar yalnızca bir kez gösterilir. Kimlik doğrulama cihazınıza erişiminizi kaybederseniz hesabınıza erişmek için bu kodları kullanabilirsiniz.')}
          </Notice>
          <div className="grid grid-cols-2 gap-2 rounded-lg border border-border bg-surface-2 p-4 font-mono text-sm">
            {codes.map((code) => <span key={code} className="select-all text-text-heading">{code}</span>)}
          </div>
          <div className="flex items-center gap-3">
            <Button ref={copyRef} type="button" variant="outline" onClick={() => { void copy() }}>{tx('Kodları kopyala')}</Button>
            <span role="status" className="text-sm text-text-muted">{copied ? tx('Kopyalandı!') : ''}</span>
          </div>
          <CheckField label={tx('Kodları güvenli bir yere kaydettim.')} checked={saved} onChange={(event) => setSaved(event.target.checked)} />
        </DialogBody>
        <DialogFooter>
          <Button type="button" disabled={!saved} onClick={onClose}>{tx('Tamam')}</Button>
        </DialogFooter>
      </DialogPopup>
    </Dialog.Portal>
  </Dialog.Root>
}
