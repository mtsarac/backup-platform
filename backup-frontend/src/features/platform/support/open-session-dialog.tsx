import { tx, useLanguage } from '@/lib/i18n'
import { useEffect, useId, useRef, useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { Button } from '@/components/ui/button'
import { Field } from '@/components/ui/field'
import { ErrorNotice } from '@/components/ui/notice'
import { ApiError } from '@/lib/api/client'
import { platformPaths } from '../paths'
import { FormDialog } from '@/components/ui/form-dialog'
import { inputClass, linkClass } from '@/components/ui/form-styles'
import { useOpenSupportSession } from './hooks'
import { permissionDescriptions, permissionLabels } from './presentation'
import type { SupportPermission } from './types'

const permissions: SupportPermission[] = ['ViewBackupMetadata', 'DownloadBackup', 'RestoreBackup']
const durationErrorMessage = 'Süre 1 ile 240 dakika arasında tam sayı olmalıdır.'

/** Conditional mounting gives each opening a fresh idempotency key and form. */
export function OpenSessionDialog({ tenantId, tenantName, onClose }: { tenantId: string; tenantName: string; onClose: () => void }) {
  useLanguage()
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
    const nextReasonError = !trimmedReason ? tx('Destek erişimi için gerekçe girin.') : trimmedReason.length > 500 ? tx('Gerekçe en fazla 500 karakter olabilir.') : ''
    const nextPermissionError = selected.length === 0 ? tx('En az bir izin seçin.') : ''
    const nextDurationError = !Number.isInteger(minutes) || minutes < 1 || minutes > 240 ? tx(durationErrorMessage) : ''
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

  return <FormDialog title={tx("Destek oturumu aç")} description={tx("“{0}” tenant'ına süreli ve gerekçeli destek erişimi verilir. İşlemler audit kayıtlarına yazılır ve tenant yöneticileri tarafından görülebilir.", { "0": tenantName })}
    submitLabel={tx("Oturumu aç")} pendingLabel={tx("Oturum açılıyor…")} pending={open.isPending} onSubmit={submit} onClose={close}>
    <fieldset disabled={open.isPending} className="space-y-4">
      <Field label={tx("Tenant")}>{control => <input {...control} className={inputClass} value={tenantName} readOnly />}</Field>
      <Field label={tx("Erişim gerekçesi")} hint={tx("Gerekçe zorunludur; en fazla 500 karakter.")} error={reasonError}>
        {control => <textarea {...control} className={inputClass} rows={3} maxLength={500} value={reason}
          onChange={event => { setReason(event.target.value); setReasonError('') }} />}
      </Field>
      <fieldset className="space-y-2" aria-describedby={`${formId}-permission-help${permissionError ? ` ${formId}-permission-error` : ''}`}>
        <legend className="mb-2 text-sm font-medium text-text-heading">{tx("Erişim izinleri")}</legend>
        {permissions.map(permission => <label key={permission} className="flex min-h-11 items-start gap-3 rounded-lg bg-surface-2 p-3 text-sm text-text">
          <input type="checkbox" className="mt-1 size-4 shrink-0 accent-primary" checked={selected.includes(permission)}
            disabled={permission === 'ViewBackupMetadata' && viewRequired} onChange={event => togglePermission(permission, event.target.checked)} />
          <span className="min-w-0"><span className="block font-medium">{permissionLabels[permission]}</span><span className="mt-0.5 block text-text-muted">{permissionDescriptions[permission]}</span></span>
        </label>)}
        <p id={`${formId}-permission-help`} className="text-xs text-text-muted">{tx("İndirme ve geri yükleme izinleri ViewBackupMetadata iznini gerektirir; bu izin otomatik seçilir.")}</p>
        {permissionError && <p id={`${formId}-permission-error`} role="alert" className="text-sm text-error">{tx(permissionError)}</p>}
      </fieldset>
      <Field label={tx("Süre (dakika)")} hint={tx("En fazla 240 dakika. Varsayılan süre 60 dakikadır.")} error={durationError}>
        {control => <input {...control} type="number" min={1} max={240} step={1} list={`${formId}-durations`} className={inputClass} value={duration}
          onInvalid={event => { event.preventDefault(); setDurationError(tx(durationErrorMessage)) }}
          onChange={event => { setDuration(event.target.value); setDurationError('') }} />}
      </Field>
      <datalist id={`${formId}-durations`}>{[15, 30, 60, 120, 240].map(minutes => <option key={minutes} value={minutes} />)}</datalist>
      <div className="flex flex-wrap gap-2" aria-label={tx("Süre seçenekleri")}>{[15, 30, 60, 120, 240].map(minutes => <Button key={minutes} type="button" size="sm" variant={duration === String(minutes) ? 'default' : 'outline'} onClick={() => { setDuration(String(minutes)); setDurationError('') }}>{minutes}{' '}{tx("dk")}</Button>)}</div>
    </fieldset>
    {open.error && (conflict
      ? <ErrorNotice what={tx('Bu tenant için zaten etkin bir destek oturumunuz var.')} dataImpact={tx('Yeni oturum açılmadı; müşteri verilerine erişim değişmedi.')} action={tx('Mevcut oturumu kullanın veya kapatın.')}
        control={conflict.sessionId ? <Link className={linkClass} to={platformPaths.supportSession(conflict.sessionId)}>{tx("Mevcut destek oturumuna git")}</Link> : undefined} />
      : <ErrorNotice error={open.error} />)}
  </FormDialog>
}
