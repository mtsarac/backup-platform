import { useEffect, useId, useRef, useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { ApiError } from '@/lib/api/client'
import { errorMessage } from '@/lib/api/failure-messages'
import { platformPaths } from '../paths'
import { FormDialog } from '../plans/form-dialog'
import { inputClass, linkClass } from '../styles'
import { useOpenSupportSession } from './hooks'
import { permissionLabels } from './presentation'
import type { SupportPermission } from './types'

const permissions: SupportPermission[] = ['ViewBackupMetadata', 'DownloadBackup', 'RestoreBackup']
const durationErrorMessage = 'Süre 1 ile 240 dakika arasında tam sayı olmalıdır.'

/** Conditional mounting gives each opening a fresh idempotency key and form. */
export function OpenSessionDialog({ tenantId, tenantName, onClose }: { tenantId: string; tenantName: string; onClose: () => void }) {
  const navigate = useNavigate()
  const formId = useId()
  const open = useOpenSupportSession()
  const submitting = useRef(false)
  const mounted = useRef(true)
  const [idempotencyKey, setIdempotencyKey] = useState(() => crypto.randomUUID())
  const [reason, setReason] = useState('')
  const [selected, setSelected] = useState<SupportPermission[]>(['ViewBackupMetadata'])
  const [duration, setDuration] = useState('60')
  const [reasonError, setReasonError] = useState('')
  const [permissionError, setPermissionError] = useState('')
  const [durationError, setDurationError] = useState('')
  const viewRequired = selected.includes('DownloadBackup') || selected.includes('RestoreBackup')
  const conflict = open.error instanceof ApiError && open.error.code === 'active_session_exists' ? open.error : null

  useEffect(() => {
    mounted.current = true
    return () => { mounted.current = false }
  }, [])

  function togglePermission(permission: SupportPermission, checked: boolean) {
    setPermissionError('')
    setSelected((current) => {
      if (!checked) {
        if (permission === 'ViewBackupMetadata' && (current.includes('DownloadBackup') || current.includes('RestoreBackup'))) return current
        return current.filter((value) => value !== permission)
      }
      const next = current.includes(permission) ? current : [...current, permission]
      return permission !== 'ViewBackupMetadata' && !next.includes('ViewBackupMetadata') ? ['ViewBackupMetadata', ...next] : next
    })
  }

  function submit() {
    if (submitting.current || open.isPending) return
    const trimmedReason = reason.trim()
    const minutes = Number(duration)
    const nextReasonError = !trimmedReason ? 'Destek erişimi için gerekçe girin.' : trimmedReason.length > 500 ? 'Gerekçe en fazla 500 karakter olabilir.' : ''
    const nextPermissionError = selected.length === 0 ? 'En az bir izin seçin.' : ''
    const nextDurationError = !Number.isInteger(minutes) || minutes < 1 || minutes > 240 ? durationErrorMessage : ''
    setReasonError(nextReasonError)
    setPermissionError(nextPermissionError)
    setDurationError(nextDurationError)
    if (nextReasonError || nextPermissionError || nextDurationError) return

    submitting.current = true
    open.mutate({ input: { tenantId, reason: trimmedReason, permissions: selected, durationMinutes: minutes }, idempotencyKey }, {
      onSuccess: (session) => {
        if (!mounted.current) return
        setIdempotencyKey(crypto.randomUUID())
        onClose()
        navigate(platformPaths.supportSession(session.id))
      },
      onSettled: () => { submitting.current = false },
    })
  }

  function close() {
    if (!submitting.current) onClose()
  }

  return <FormDialog title="Destek oturumu aç" description={`“${tenantName}” tenant'ına süreli ve gerekçeli destek erişimi verilir. İşlemler audit kayıtlarına yazılır ve tenant yöneticileri tarafından görülebilir.`}
    submitLabel="Oturumu aç" pendingLabel="Oturum açılıyor…" pending={open.isPending} onSubmit={submit} onClose={close}>
    <fieldset disabled={open.isPending} className="space-y-4">
      <div>
        <label htmlFor={`${formId}-tenant`} className="block text-sm text-text-muted">Tenant</label>
        <input id={`${formId}-tenant`} className={inputClass} value={tenantName} readOnly />
      </div>
      <div>
        <label htmlFor={`${formId}-reason`} className="block text-sm text-text-muted">Erişim gerekçesi</label>
        <textarea id={`${formId}-reason`} className={inputClass} rows={3} maxLength={500} value={reason}
          aria-invalid={Boolean(reasonError)} aria-describedby={`${formId}-reason-help${reasonError ? ` ${formId}-reason-error` : ''}`}
          onChange={(event) => { setReason(event.target.value); setReasonError('') }} />
        <p id={`${formId}-reason-help`} className="mt-1 text-xs text-text-muted">Gerekçe zorunludur; en fazla 500 karakter.</p>
        {reasonError && <p id={`${formId}-reason-error`} role="alert" className="mt-1 text-sm text-error">{reasonError}</p>}
      </div>
      <fieldset className="space-y-2" aria-describedby={`${formId}-permission-help${permissionError ? ` ${formId}-permission-error` : ''}`}>
        <legend className="mb-2 text-sm font-medium text-text-heading">Erişim izinleri</legend>
        {permissions.map((permission) => <label key={permission} className="flex items-start gap-2 text-sm text-text">
          <input type="checkbox" className="mt-1" checked={selected.includes(permission)}
            disabled={permission === 'ViewBackupMetadata' && viewRequired} onChange={(event) => togglePermission(permission, event.target.checked)} />
          <span>{permissionLabels[permission]}</span>
        </label>)}
        <p id={`${formId}-permission-help`} className="text-xs text-text-muted">İndirme ve geri yükleme izinleri ViewBackupMetadata iznini gerektirir; bu izin otomatik seçilir.</p>
        {permissionError && <p id={`${formId}-permission-error`} role="alert" className="text-sm text-error">{permissionError}</p>}
      </fieldset>
      <div>
        <label htmlFor={`${formId}-duration`} className="block text-sm text-text-muted">Süre (dakika)</label>
        <input id={`${formId}-duration`} type="number" min={1} max={240} step={1} list={`${formId}-durations`} className={inputClass} value={duration}
          aria-invalid={Boolean(durationError)} aria-describedby={`${formId}-duration-help${durationError ? ` ${formId}-duration-error` : ''}`}
          onInvalid={(event) => { event.preventDefault(); setDurationError(durationErrorMessage) }}
          onChange={(event) => { setDuration(event.target.value); setDurationError('') }} />
        <datalist id={`${formId}-durations`}>{[15, 30, 60, 120, 240].map((minutes) => <option key={minutes} value={minutes} />)}</datalist>
        <p id={`${formId}-duration-help`} className="mt-1 text-xs text-text-muted">En fazla 240 dakika. Varsayılan süre 60 dakikadır.</p>
        {durationError && <p id={`${formId}-duration-error`} role="alert" className="mt-1 text-sm text-error">{durationError}</p>}
      </div>
    </fieldset>
    {open.error && <div role="alert" className="space-y-1 text-sm text-error">
      <p>{conflict ? 'Bu tenant için zaten etkin bir destek oturumunuz var.' : errorMessage(open.error)}</p>
      {conflict?.sessionId && <Link className={linkClass} to={platformPaths.supportSession(conflict.sessionId)}>Mevcut destek oturumuna git</Link>}
    </div>}
  </FormDialog>
}
