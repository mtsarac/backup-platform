import { tx, useLanguage } from '@/lib/i18n'
import { useRef, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { Field } from '@/components/ui/field'
import { Notice } from '@/components/ui/notice'
import { errorMessage } from '@/lib/api/failure-messages'
import { platformPaths } from '@/features/platform/paths'
import { FormDialog } from '@/features/platform/plans/form-dialog'
import { inputClass } from '@/features/platform/styles'
import { useCreateBreakGlass } from './hooks'

const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

/** Mounted only while open, so every opening starts clean. */
export function BreakGlassRequestDialog({ initialTenantId = '', initialArtifactId = '', onClose }: { initialTenantId?: string; initialArtifactId?: string; onClose: () => void }) {
  useLanguage()
  const navigate = useNavigate()
  const create = useCreateBreakGlass()
  const submitting = useRef(false)
  const [tenantId, setTenantId] = useState(initialTenantId)
  const [artifactId, setArtifactId] = useState(initialArtifactId)
  const [reason, setReason] = useState('')
  const [ttl, setTtl] = useState('60')
  const [errors, setErrors] = useState<Record<string, string>>({})

  function submit() {
    if (submitting.current) return
    const minutes = Number(ttl)
    const found: Record<string, string> = {}
    if (!guid.test(tenantId.trim())) found.tenantId = tx('Geçerli bir tenant kimliği (GUID) girin.')
    if (!guid.test(artifactId.trim())) found.artifactId = tx('Geçerli bir Backup kimliği (GUID) girin.')
    if (!reason.trim()) found.reason = tx('Break-glass için gerekçe girin.')
    else if (reason.trim().length > 500) found.reason = tx('Gerekçe en fazla 500 karakter olabilir.')
    if (!Number.isInteger(minutes) || minutes < 1 || minutes > 240) found.ttl = tx('Süre 1 ile 240 dakika arasında tam sayı olmalıdır.')
    setErrors(found)
    if (Object.keys(found).length) return
    submitting.current = true
    create.mutate({ tenantId: tenantId.trim(), artifactId: artifactId.trim(), reason: reason.trim(), ttlMinutes: minutes }, {
      onSuccess: request => { onClose(); navigate(platformPaths.breakGlassDetail(request.id)) },
      onSettled: () => { submitting.current = false },
    })
  }

  return <FormDialog title={tx("Break-glass isteği aç")} description={tx("Şifreli bir Backup'ın tenant anahtarıyla çözülmesini ister. Farklı bir PlatformAdmin onaylamadan hiçbir şey çözülmez; onay süreli ve tek kullanımlıktır. Her adım audit kaydına yazılır ve tenant sahiplerine bildirilir.")}
    submitLabel={tx("İstek aç")} pendingLabel={tx("İstek açılıyor…")} pending={create.isPending} onSubmit={submit} onClose={onClose}>
    <fieldset disabled={create.isPending} className="space-y-4">
      <Field label={tx("Tenant kimliği")} error={errors.tenantId}>{control => <input {...control} className={inputClass} value={tenantId} spellCheck={false} onChange={event => setTenantId(event.target.value)} />}</Field>
      <Field label={tx("Backup kimliği")} error={errors.artifactId}>{control => <input {...control} className={inputClass} value={artifactId} spellCheck={false} onChange={event => setArtifactId(event.target.value)} />}</Field>
      <Field label={tx("Gerekçe")} hint={tx("Zorunludur; en fazla 500 karakter. Tenant sahipleri bu gerekçeyi görür.")} error={errors.reason}>
        {control => <textarea {...control} className={inputClass} rows={3} maxLength={500} value={reason} onChange={event => setReason(event.target.value)} />}
      </Field>
      <Field label={tx("Süre (dakika)")} hint={tx("Onay ve kullanım bu süre içinde olmalıdır. En fazla 240, varsayılan 60.")} error={errors.ttl}>
        {control => <input {...control} type="number" step={1} className={inputClass} value={ttl} onChange={event => setTtl(event.target.value)} />}
      </Field>
    </fieldset>
    {create.error && <Notice tone="error">{errorMessage(create.error)}</Notice>}
  </FormDialog>
}
