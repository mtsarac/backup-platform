import { useEffect, useMemo, useRef, useState } from 'react'
import { Dialog } from '@base-ui/react/dialog'
import { Button } from '@/components/ui/button'
import { Field } from '@/components/ui/field'
import { Notice } from '@/components/ui/notice'
import { inputClass } from '@/features/platform/styles'
import { CopyButton } from './copy-button'
import { createEnrollmentToken } from './api'
import type { CreatedEnrollmentToken } from './types'
import { agentDate } from './format'
import { errorMessage } from '@/lib/api/failure-messages'
import { useEnrollmentMonitor } from './use-enrollment-monitor'

/** Mounted only while open. Plaintext exists in local state, never in a query/mutation cache. */
export function CreateTokenDialog({ tenantId, onClose, onCreated }: { tenantId: string; onClose: () => void; onCreated: () => void }) {
  const [lifetimeMinutes, setLifetimeMinutes] = useState('1440')
  const [created, setCreated] = useState<CreatedEnrollmentToken | null>(null)
  const [pending, setPending] = useState(false)
  const [error, setError] = useState('')
  const request = useRef<AbortController | null>(null)
  const lifetimeRef = useRef<HTMLInputElement>(null)
  const tokenId = created?.tokenId
  const expiresAt = created?.expiresAt
  const ticket = useMemo(() => tokenId && expiresAt ? { tokenId, expiresAt } : null, [tokenId, expiresAt])
  const monitor = useEnrollmentMonitor(tenantId, ticket, () => setCreated(value => value ? { ...value, token: '' } : null))
  useEffect(() => () => { request.current?.abort() }, [])
  function close() {
    request.current?.abort()
    setCreated(null)
    onClose()
  }
  async function create() {
    if (pending || created) return
    const controller = new AbortController()
    request.current = controller
    setError('')
    setPending(true)
    try {
      const result = await createEnrollmentToken(tenantId, Number(lifetimeMinutes), controller.signal)
      if (!controller.signal.aborted) {
        setCreated(result)
        onCreated()
      }
    } catch (cause) {
      if (!controller.signal.aborted) setError(errorMessage(cause))
    } finally {
      if (!controller.signal.aborted) setPending(false)
    }
  }
  return <Dialog.Root open onOpenChange={(open) => { if (!open) close() }}>
    <Dialog.Portal><Dialog.Backdrop className="fixed inset-0 z-40 bg-black/50" />
      <Dialog.Popup initialFocus={lifetimeRef} className="fixed left-1/2 top-1/2 z-50 max-h-[90dvh] w-[calc(100%-2rem)] max-w-lg -translate-x-1/2 -translate-y-1/2 space-y-4 overflow-y-auto rounded-xl border border-border bg-surface p-6 shadow-xl">
        <Dialog.Title className="text-lg font-semibold text-text-heading">Kayıt token'ı oluştur</Dialog.Title>
        <Dialog.Description className="text-sm text-text-muted">Token yalnızca agent kaydı için kullanılır. Güvenli bir kanaldan paylaşın. Bu token tekrar görüntülenemez.</Dialog.Description>
        {created ? (
          <div className="space-y-4">
            <Notice tone="success">{created.token ? 'Token oluşturuldu.' : monitor.phase === 'Checking' ? 'Token gizlendi; kayıt durumu doğrulanıyor.' : monitor.phase === 'Expired' ? 'Token süresi doldu.' : monitor.phase === 'Revoked' ? 'Token iptal edildi.' : 'Token kullanıldı.'}</Notice>
            {created.token && <div className="space-y-2">
              <Field label="Tek seferlik kayıt token'ı">{(control) => <textarea {...control} readOnly rows={3} value={created.token} className="mt-1 block w-full resize-none break-all rounded-lg border border-border-strong/70 bg-surface-2 p-3 font-mono text-sm text-text" />}</Field>
              <CopyButton value={created.token} label="Kayıt token'ını kopyala" />
            </div>}
            <p className="text-sm text-text-muted">Son kullanma: {agentDate(created.expiresAt)}</p>
            <p className="text-sm font-medium text-text">Bu token tekrar görüntülenemez.</p>
          </div>
        ) : (
          <form id="create-enrollment-token" onSubmit={(event) => { event.preventDefault(); void create() }}>
            <Field label="Geçerlilik süresi (dakika)" hint="5 dakika ile 7 gün arasında. Varsayılan: 24 saat.">{(control) => <input {...control} ref={lifetimeRef} type="number" required min={5} max={10080} step={1} disabled={pending} value={lifetimeMinutes} onChange={(event) => setLifetimeMinutes(event.target.value)} className={inputClass} />}</Field>
          </form>
        )}
        {error && <Notice tone="error">{error}</Notice>}
        {monitor.error && <Notice tone="error">{monitor.error}</Notice>}
        {monitor.paused && <Button variant="outline" onClick={monitor.retry}>Takibi tekrar dene</Button>}
        <div className="flex flex-wrap justify-end gap-2"><Dialog.Close render={<Button variant="outline" />}>Kapat</Dialog.Close>{!created && <Button type="submit" form="create-enrollment-token" disabled={pending}>{pending ? 'Oluşturuluyor…' : 'Token oluştur'}</Button>}</div>
      </Dialog.Popup>
    </Dialog.Portal>
  </Dialog.Root>
}
