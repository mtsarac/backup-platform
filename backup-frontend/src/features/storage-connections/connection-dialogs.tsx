import { useEffect, useState } from 'react'
import { FormDialog } from '@/features/platform/plans/form-dialog'
import { inputClass } from '@/features/platform/styles'
import { QUOTA_SCOPE_PATTERN } from '@/features/storage/scope-input'
import { ConfigFieldInputs, SecretFieldInputs } from './descriptor-fields'
import { buildConfiguration, buildSecretValues, configurationComplete, hasSecretInput, initialFieldValues, secretsComplete } from './descriptor-values'
import type { FieldValues } from './descriptor-values'
import type { ConnectionOps } from './hooks'
import { connectionErrorMessage, errorField, invalidFieldName } from './labels'
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
  const { canEdit, canSetQuotaScope } = connection.permissions
  const [name, setName] = useState(connection.name)
  const [quotaScope, setQuotaScope] = useState(connection.quotaScope)
  const [values, setValues] = useState<FieldValues>(() => initialFieldValues(configFields, connection.configuration))
  const initialValues = initialFieldValues(configFields, connection.configuration)
  const mutation = ops.update

  const input: PlatformUpdateConnectionInput = {}
  if (canEdit && name.trim() !== connection.name) input.name = name.trim()
  if (canEdit && configFields.length > 0 && configFields.some((field) => values[field.name] !== initialValues[field.name])) input.configuration = buildConfiguration(configFields, values)
  if (canSetQuotaScope && quotaScope.trim() !== connection.quotaScope) input.quotaScope = quotaScope.trim()
  const quotaScopeInvalid = canSetQuotaScope && !QUOTA_SCOPE_PATTERN.test(quotaScope.trim())
  const valid = (!canEdit || (name.trim() !== '' && configurationComplete(configFields, values))) && !quotaScopeInvalid

  return <FormDialog title={`Bağlantıyı düzenle: ${connection.name}`} submitLabel="Kaydet" pendingLabel="Kaydediliyor…" pending={mutation.isPending}
    disabled={Object.keys(input).length === 0 || !valid} error={mutation.error ? connectionErrorMessage(mutation.error) : undefined}
    onSubmit={() => mutation.mutate({ connection, input }, { onSuccess: onClose })} onClose={onClose}>
    <label className="block text-sm text-text-muted">Ad
      <input className={inputClass} value={name} required disabled={!canEdit} aria-invalid={errorField(mutation.error) === 'name'} onChange={(event) => setName(event.target.value)} />
    </label>
    {canEdit && configFields.length > 0 && <ConfigFieldInputs fields={configFields} values={values} invalidName={invalidFieldName(mutation.error, 'configuration')} onChange={(field, value) => setValues((current) => ({ ...current, [field]: value }))} />}
    {canSetQuotaScope && <label className="block text-sm text-text-muted">Kota kapsamı
      <input className={inputClass} value={quotaScope} maxLength={64} required aria-invalid={quotaScopeInvalid} onChange={(event) => setQuotaScope(event.target.value)} />
      <span className="mt-1 block text-xs">Kota Politikası seçicileri bu etikete göre eşleşir (küçük harf, rakam ve “-”, 1-64 karakter). Değiştirmek tenant'ın kullanımını yeniden hesaplar.</span>
      {quotaScopeInvalid && <span role="alert" className="mt-1 block text-xs text-error">Kota kapsamı yalnızca küçük harf, rakam ve “-” içerebilir (1-64 karakter).</span>}
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
 * or failure alike). The inputs are password-typed and never prefilled: a stored secret is never read back from the API.
 */
export function SecretDialog({ connection, fields, ops, onClose }: SecretProps) {
  const [values, setValues] = useState<Record<string, string>>({})
  const mutation = ops.setSecret
  const reset = mutation.reset
  // The mutation's variables hold the submitted values: drop them when the dialog goes away.
  useEffect(() => reset, [reset])

  function submit() {
    const secret = buildSecretValues(fields, values)
    setValues({})
    mutation.mutate({ connection, values: secret }, { onSuccess: () => { reset(); onClose() } })
  }

  return <FormDialog title={`${connection.hasSecret ? 'Kimlik bilgisini yenile' : 'Kimlik bilgisi ayarla'}: ${connection.name}`}
    description="Değerler yalnızca sunucuya gönderilir; kaydedildikten sonra bir daha gösterilmez. Hata olursa alanları yeniden girmeniz gerekir."
    submitLabel="Kaydet" pendingLabel="Kaydediliyor…" pending={mutation.isPending}
    disabled={!hasSecretInput(values) || !secretsComplete(fields, values)} error={mutation.error ? connectionErrorMessage(mutation.error) : undefined}
    onSubmit={submit} onClose={onClose}>
    <SecretFieldInputs fields={fields} values={values} invalidName={invalidFieldName(mutation.error, 'secret')} onChange={(field, value) => setValues((current) => ({ ...current, [field]: value }))} />
  </FormDialog>
}
