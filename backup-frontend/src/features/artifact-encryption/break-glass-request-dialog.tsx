import { tx, useLanguage } from '@/lib/i18n'
import { useEffect, useRef, useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { useQueryClient } from '@tanstack/react-query'
import { Field } from '@/components/ui/field'
import { ErrorNotice } from '@/components/ui/notice'
import { platformPaths } from '@/features/platform/paths'
import { FormDialog } from '@/components/ui/form-dialog'
import { inputClass } from '@/components/ui/form-styles'
import { TenantPicker } from '@/features/platform/tenant-picker'
import { BackupPicker } from '@/features/artifacts/backup-picker'
import { removeSupportData, useSupportSession, useSupportSessions } from '@/features/platform/support/hooks'
import { supportAccess } from '@/features/platform/support/presentation'
import { useSupportCountdown } from '@/features/platform/support/use-support-countdown'
import { useCreateBreakGlass } from './hooks'
import { QueryState } from '@/components/data/query-state'
import { errorMessage } from '@/lib/api/failure-messages'


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
    if (!tenantId) found.tenantId = tx('Tenant seçin')
    if (!artifactId) found.artifactId = tx('Yedek dosyası seçin')
    if (!reason.trim()) found.reason = tx('Acil destek erişimi için gerekçe girin.')
    else if (reason.trim().length > 500) found.reason = tx('Gerekçe en fazla 500 karakter olabilir.')
    if (!Number.isInteger(minutes) || minutes < 1 || minutes > 240) found.ttl = tx('Süre 1 ile 240 dakika arasında tam sayı olmalıdır.')
    setErrors(found)
    if (Object.keys(found).length) return
    submitting.current = true
    create.mutate({ tenantId, artifactId, reason: reason.trim(), ttlMinutes: minutes }, {
      onSuccess: request => { onClose(); navigate(platformPaths.breakGlassDetail(request.id)) },
      onSettled: () => { submitting.current = false },
    })
  }

  return <FormDialog title={tx("Acil destek erişimi iste")} description={tx("Şifreli bir yedek dosyasının tenant anahtarıyla çözülmesini ister. Farklı bir PlatformAdmin onaylamadan hiçbir şey çözülmez; onay süreli ve tek kullanımlıdır. Her adım denetim kaydına yazılır ve tenant sahiplerine bildirilir.")}
    submitLabel={tx("İstek aç")} pendingLabel={tx("İstek açılıyor…")} pending={create.isPending} onSubmit={submit} onClose={onClose}>
    <fieldset disabled={create.isPending} className="space-y-4">
      <TenantPicker value={tenantId} onChange={id => { setTenantId(id); setArtifactId('') }} />
      {errors.tenantId && <p role="alert" className="text-sm text-error">{errors.tenantId}</p>}
      {tenantId && <EmergencyBackupPicker key={tenantId} tenantId={tenantId} value={artifactId} onChange={setArtifactId} />}
      {errors.artifactId && <p role="alert" className="text-sm text-error">{errors.artifactId}</p>}
      <Field label={tx("Gerekçe")} hint={tx("Zorunludur; en fazla 500 karakter. Tenant sahipleri bu gerekçeyi görür.")} error={errors.reason}>
        {control => <textarea {...control} className={inputClass} rows={3} maxLength={500} value={reason} onChange={event => setReason(event.target.value)} />}
      </Field>
      <Field label={tx("Süre (dakika)")} hint={tx("Onay ve kullanım bu süre içinde olmalıdır. En fazla 240, varsayılan 60.")} error={errors.ttl}>
        {control => <input {...control} type="number" step={1} className={inputClass} value={ttl} onChange={event => setTtl(event.target.value)} />}
      </Field>
    </fieldset>
    {create.error && <ErrorNotice error={create.error} />}
  </FormDialog>
}

/** Platform authority alone cannot read customer metadata; use an already-authorized support session. */
function EmergencyBackupPicker({ tenantId, value, onChange }: { tenantId: string; value: string; onChange: (id: string) => void }) {
  const client = useQueryClient()
  const sessions = useSupportSessions({ tenantId, status: 'Active', mine: true, page: 1, pageSize: 100 })
  const found = sessions.data?.items.find(session => session.tenantId === tenantId && supportAccess(session).view && Date.parse(session.expiresAt) > Date.parse(session.serverNow))
  const detail = useSupportSession(found?.id ?? '')
  const session = detail.data ?? found
  const countdown = useSupportCountdown(session?.expiresAt ?? '', session?.serverNow ?? '', detail.dataUpdatedAt || sessions.dataUpdatedAt)
  const enabled = Boolean(!sessions.error && !detail.error && session && session.tenantId === tenantId && supportAccess(session).view && countdown.seconds > 0)
  useEffect(() => {
    if (session && !enabled) removeSupportData(client, session.id)
  }, [client, enabled, session])
  return <div className="space-y-2">
    <BackupPicker tenantId={tenantId} value={value} onChange={onChange} supportScope={{ tenantId, sessionId: session?.id ?? '', enabled }} />
    {(sessions.error || detail.error) && <QueryState forbiddenMessage={errorMessage} isPending={false} error={sessions.error ?? detail.error} />}
    {!sessions.isPending && !enabled && <p className="text-sm text-text-muted">{tx("Yedek dosyalarını adla seçmek için bu müşteri üzerinde yedek bilgilerini görüntüleme izinli bir destek oturumu açın.")}{' '}
      <Link className="text-primary underline" to={`${platformPaths.supportSessions}?tenantId=${encodeURIComponent(tenantId)}`}>{tx("Destek erişimi")}</Link>
    </p>}
  </div>
}
