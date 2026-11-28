import { tx, useLanguage } from '@/lib/i18n'
import { TextInput } from '@/components/ui/text-input'
import { RequiredText } from '@/components/ui/field'
import { useEffect, useState } from 'react'
import { FormDialog } from '@/components/ui/form-dialog'
import { QUOTA_SCOPE_PATTERN } from '@/features/storage/scope-input'
import { ConfigFieldInputs, SecretFieldInputs } from './descriptor-fields'
import { activeSecretFields, buildConfiguration, buildSecretValues, configurationComplete, editableConfigFields, hasSecretInput, initialFieldValues, secretsComplete } from './descriptor-values'
import type { FieldValues } from './descriptor-values'
import type { ConnectionOps } from './hooks'
import { connectionDisplayName, errorField, invalidFieldName } from './labels'
import type { ConfigField, ConnectionView, PlatformUpdateConnectionInput, SecretField } from './types'

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
  const editableFields = editableConfigFields(connection.providerType, configFields)
  const [name, setName] = useState(connection.name)
  const [quotaScope, setQuotaScope] = useState(connection.quotaScope)
  const [values, setValues] = useState<FieldValues>(() => initialFieldValues(editableFields, connection.configuration, connection.providerType))
  const initialValues = initialFieldValues(editableFields, connection.configuration, connection.providerType)
  const mutation = ops.update

  const input: PlatformUpdateConnectionInput = {}
  if (canEdit && name.trim() !== connection.name) input.name = name.trim()
  if (canEdit && editableFields.length > 0 && editableFields.some((field) => values[field.name] !== initialValues[field.name])) {
    const editableNames = new Set(editableFields.map((field) => field.name))
    const preserved = Object.fromEntries(configFields.filter((field) => !editableNames.has(field.name) && Boolean(connection.configuration[field.name]?.trim())).map((field) => [field.name, connection.configuration[field.name]!.trim()]))
    input.configuration = { ...preserved, ...buildConfiguration(editableFields, values) }
  }
  if (canSetQuotaScope && quotaScope.trim() !== connection.quotaScope) input.quotaScope = quotaScope.trim()
  const quotaScopeInvalid = canSetQuotaScope && !QUOTA_SCOPE_PATTERN.test(quotaScope.trim())
  const valid = (!canEdit || (name.trim() !== '' && configurationComplete(editableFields, values))) && !quotaScopeInvalid

  return <FormDialog title={tx("Bağlantıyı düzenle: {0}", { "0": connectionDisplayName(connection) })} submitLabel={tx("Kaydet")} pendingLabel={tx("Kaydediliyor…")} pending={mutation.isPending}
    disabled={Object.keys(input).length === 0 || !valid} error={mutation.error} errorScope="storageConnection"
    onSubmit={() => mutation.mutate({ connection, input }, { onSuccess: onClose })} onClose={onClose}>
    <label className="block text-sm text-text-muted"><RequiredText>{tx("Ad")}</RequiredText><TextInput className="mt-1" value={name} required disabled={!canEdit} aria-invalid={errorField(mutation.error) === 'name'} onChange={(event) => setName(event.target.value)} />
    </label>
    {canEdit && editableFields.length > 0 && <ConfigFieldInputs providerType={connection.providerType} fields={editableFields} values={values} invalidName={invalidFieldName(mutation.error, 'configuration')} onChange={(field, value) => setValues((current) => ({ ...current, [field]: value }))} />}
    {canEdit && connection.providerType === 'sftp' && <p className="text-xs text-text-muted">{tx("Kimlik doğrulama yöntemini değiştirdikten sonra yeni yöntem için kimlik bilgisi ayarlayın ve bağlantıyı test edin. Kullanımdaki bir bağlantının sunucu, kullanıcı, dizin ve kimlik doğrulama yöntemi değiştirilemez.")}</p>}
    {canSetQuotaScope && <label className="block text-sm text-text-muted"><RequiredText>{tx("Kota kapsamı")}</RequiredText><TextInput className="mt-1" value={quotaScope} maxLength={64} required aria-invalid={quotaScopeInvalid} onChange={(event) => setQuotaScope(event.target.value)} />
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

  return <FormDialog title={`${connection.hasSecret ? tx('Kimlik bilgisini yenile') : tx('Kimlik bilgisi ayarla')}: ${connectionDisplayName(connection)}`}
    description={tx("Değerler yalnızca sunucuya gönderilir; kaydedildikten sonra bir daha gösterilmez. Hata olursa alanları yeniden girmeniz gerekir.")}
    submitLabel={tx("Kaydet")} pendingLabel={tx("Kaydediliyor…")} pending={mutation.isPending}
    disabled={!hasSecretInput(buildSecretValues(activeFields, values)) || !secretsComplete(activeFields, values)} error={mutation.error} errorScope="storageConnection"
    onSubmit={submit} onClose={onClose}>
    {connection.providerType === 'sftp' && <p className="text-xs text-text-muted">{tx("Bu işlem kayıtlı kimlik bilgisinin tamamını değiştirir. Yeni")}{' '}{connection.configuration.authenticationType === 'private-key' ? tx('özel anahtarı ve gerekiyorsa parolasını') : tx('parolayı')}{' '}{tx("girin. Boş anahtar parolası eski parolayı korumaz.")}</p>}
    <SecretFieldInputs providerType={connection.providerType} fields={activeFields} savedNames={connection.secretFieldNames} values={values} invalidName={invalidFieldName(mutation.error, 'secret')} onChange={(field, value) => setValues((current) => ({ ...current, [field]: value }))} />
  </FormDialog>
}
