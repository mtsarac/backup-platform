import { tx, useLanguage } from '@/lib/i18n'
import { useRef, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { Field } from '@/components/ui/field'
import { ErrorNotice } from '@/components/ui/notice'
import { platformPaths } from '@/features/platform/paths'
import { FormDialog } from '@/components/ui/form-dialog'
import { TextInput, TextArea } from '@/components/ui/text-input'
import { TenantPicker } from '@/features/platform/tenant-picker'
import { backupOption } from '@/features/artifacts/backup-option'
import { SearchablePicker } from '@/components/ui/searchable-picker'
import { useBreakGlassArtifacts, useCreateBreakGlass } from './hooks'
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
      {tenantId && <EncryptedBackupPicker key={tenantId} tenantId={tenantId} value={artifactId} onChange={setArtifactId} />}
      {errors.artifactId && <p role="alert" className="text-sm text-error">{errors.artifactId}</p>}
      <Field label={tx("Gerekçe")} hint={tx("Zorunludur; en fazla 500 karakter. Tenant sahipleri bu gerekçeyi görür.")} error={errors.reason}>
        {control => <TextArea {...control}  rows={3} maxLength={500} value={reason} onChange={event => setReason(event.target.value)} />}
      </Field>
      <Field label={tx("Süre (dakika)")} hint={tx("Onay ve kullanım bu süre içinde olmalıdır. En fazla 240, varsayılan 60.")} error={errors.ttl}>
        {control => <TextInput {...control} type="number" step={1}  value={ttl} onChange={event => setTtl(event.target.value)} />}
      </Field>
    </fieldset>
    {create.error && <ErrorNotice error={create.error} />}
  </FormDialog>
}

/** The tenant's encrypted backups, from the break-glass endpoint itself: dual control and audit are its gate, not a support session. */
function EncryptedBackupPicker({ tenantId, value, onChange }: { tenantId: string; value: string; onChange: (id: string) => void }) {
  const list = useBreakGlassArtifacts(tenantId)
  const options = (list.data?.items ?? []).map(backupOption)
  return <div className="space-y-2">
    <SearchablePicker label={tx("Yedek dosyası")} placeholder={tx("Yedek dosyası seçin")} options={options} value={value} onChange={onChange}
      selectedLabel={value && list.data && !options.some(option => option.value === value) ? tx("Seçili yedek dosyası") : undefined} disabled={list.isPending || Boolean(list.error)} />
    {list.error && <QueryState forbiddenMessage={errorMessage} isPending={false} error={list.error} />}
    {list.data && options.length === 0 && <p className="text-sm text-text-muted">{tx("Bu müşterinin şifreli yedek dosyası yok.")}</p>}
  </div>
}
