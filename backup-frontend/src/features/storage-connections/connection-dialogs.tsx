import { tx, useLanguage } from '@/lib/i18n'
import { useEffect, useState } from 'react'
import { FormDialog } from '@/features/platform/plans/form-dialog'
import { inputClass } from '@/features/platform/styles'
import { QUOTA_SCOPE_PATTERN } from '@/features/storage/scope-input'
import { ConfigFieldInputs, SecretFieldInputs } from './descriptor-fields'
import { activeSecretFields, buildConfiguration, buildSecretValues, configurationComplete, hasSecretInput, initialFieldValues, secretsComplete } from './descriptor-values'
import type { FieldValues } from './descriptor-values'
import type { ConnectionOps } from './hooks'
import { errorField, invalidFieldName } from './labels'
import type { ConfigField, ConnectionView, PlatformUpdateConnectionInput, SecretField } from './types'
import { errorMessage } from '@/lib/api/failure-messages'

interface EditProps {
  connection: ConnectionView
  /** Config fields of the connection's provider; empty when the descriptor is unknown. */
  configFields: readonly ConfigField[]
  ops: ConnectionOps
  onClose: () => void
}

/** Name and configuration for whoever may edit them, the quota scope for whoever may set it: each field follows its own permission. */
export function EditConnectionDialog({ connection, configFields, ops, onClose }: EditProps) {
  useLanguage()
  const { canEdit, canSetQuotaScope } = connection.permissions
  const [name, setName] = useState(connection.name)
  const [quotaScope, setQuotaScope] = useState(connection.quotaScope)
  const [values, setValues] = useState<FieldValues>(() => initialFieldValues(configFields, connection.configuration, connection.providerType))
  const initialValues = initialFieldValues(configFields, connection.configuration, connection.providerType)
  const mutation = ops.update

  const input: PlatformUpdateConnectionInput = {}
  if (canEdit && name.trim() !== connection.name) input.name = name.trim()
  if (canEdit && configFields.length > 0 && configFields.some((field) => values[field.name] !== initialValues[field.name])) input.configuration = buildConfiguration(configFields, values)
  if (canSetQuotaScope && quotaScope.trim() !== connection.quotaScope) input.quotaScope = quotaScope.trim()
  const quotaScopeInvalid = canSetQuotaScope && !QUOTA_SCOPE_PATTERN.test(quotaScope.trim())
  const valid = (!canEdit || (name.trim() !== '' && configurationComplete(configFields, values))) && !quotaScopeInvalid

  return <FormDialog title={tx("Bağlantıyı düzenle: {0}", { "0": connection.name })} submitLabel="Kaydet" pendingLabel="Kaydediliyor…" pending={mutation.isPending}
    disabled={Object.keys(input).length === 0 || !valid} error={mutation.error ? errorMessage(mutation.error, 'storageConnection') : undefined}
    onSubmit={() => mutation.mutate({ connection, input }, { onSuccess: onClose })} onClose={onClose}>
    <label className="block text-sm text-text-muted">{tx("Ad")}<input className={inputClass} value={name} required disabled={!canEdit} aria-invalid={errorField(mutation.error) === 'name'} onChange={(event) => setName(event.target.value)} />
    </label>
    {canEdit && configFields.length > 0 && <ConfigFieldInputs providerType={connection.providerType} fields={configFields} values={values} invalidName={invalidFieldName(mutation.error, 'configuration')} onChange={(field, value) => setValues((current) => ({ ...current, [field]: value }))} />}
    {canEdit && connection.providerType === 'sftp' && <p className="text-xs text-text-muted">{tx("Kimlik doğrulama yöntemini değiştirdikten sonra yeni yöntem için kimlik bilgisi ayarlayın ve bağlantıyı test edin. Kullanımdaki bir bağlantının sunucu, kullanıcı, dizin ve kimlik doğrulama yöntemi değiştirilemez.")}</p>}
    {canSetQuotaScope && <label className="block text-sm text-text-muted">{tx("Kota kapsamı")}<input className={inputClass} value={quotaScope} maxLength={64} required aria-invalid={quotaScopeInvalid} onChange={(event) => setQuotaScope(event.target.value)} />
      <span className="mt-1 block text-xs">{tx("Kota Politikası seçicileri bu etikete göre eşleşir (küçük harf, rakam ve “-”, 1-64 karakter). Değiştirmek tenant'ın kullanımını yeniden hesaplar.")}</span>
      {quotaScopeInvalid && <span role="alert" className="mt-1 block text-xs text-error">{tx("Kota kapsamı yalnızca küçük harf, rakam ve “-” içerebilir (1-64 karakter).")}</span>}
    </label>}
  </FormDialog>
}

interface SecretProps {
  connection: ConnectionView
  fields: readonly SecretField[]
  ops: ConnectionOps
  onClose: () => void
}

/**
 * Secret values exist only in this component's state until submit, are sent once, and are wiped from state at the moment of submit (success
 * or failure alike). Stored credentials are never read back from the API.
 */
export function SecretDialog({ connection, fields, ops, onClose }: SecretProps) {
  useLanguage()
  const [values, setValues] = useState<Record<string, string>>({})
  const activeFields = activeSecretFields(connection.providerType, connection.configuration, fields)
  const mutation = ops.setSecret
  const reset = mutation.reset
  // The mutation's variables hold the submitted values: drop them when the dialog goes away.
  useEffect(() => reset, [reset])

  function submit() {
    const secret = buildSecretValues(activeFields, values)
    setValues({})
    mutation.mutate({ connection, values: secret }, { onSuccess: () => { reset(); onClose() } })
  }

  return <FormDialog title={`${connection.hasSecret ? tx('Kimlik bilgisini yenile') : tx('Kimlik bilgisi ayarla')}: ${connection.name}`}
    description={tx("Değerler yalnızca sunucuya gönderilir; kaydedildikten sonra bir daha gösterilmez. Hata olursa alanları yeniden girmeniz gerekir.")}
    submitLabel="Kaydet" pendingLabel="Kaydediliyor…" pending={mutation.isPending}
    disabled={!hasSecretInput(buildSecretValues(activeFields, values)) || !secretsComplete(activeFields, values)} error={mutation.error ? errorMessage(mutation.error, 'storageConnection') : undefined}
    onSubmit={submit} onClose={onClose}>
    {connection.providerType === 'sftp' && <p className="text-xs text-text-muted">{tx("Bu işlem kayıtlı kimlik bilgisinin tamamını değiştirir. Yeni")}{' '}{connection.configuration.authenticationType === 'private-key' ? tx('özel anahtarı ve gerekiyorsa parolasını') : tx('parolayı')}{' '}{tx("girin. Boş anahtar parolası eski parolayı korumaz.")}</p>}
    <SecretFieldInputs providerType={connection.providerType} fields={activeFields} values={values} invalidName={invalidFieldName(mutation.error, 'secret')} onChange={(field, value) => setValues((current) => ({ ...current, [field]: value }))} />
  </FormDialog>
}
