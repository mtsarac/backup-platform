import { tx, useLanguage } from '@/lib/i18n'
import { CheckField } from '@/components/ui/check-field'
import { Select, TextInput } from '@/components/ui/text-input'
import { useId } from 'react'
import { SecretField as SecretInput } from '@/components/ui/secret-field'
import { editableConfigFields, type FieldValues } from './descriptor-values'
import type { ConfigField, SecretField } from './types'

function configLabel(field: ConfigField, providerType?: string) {
  if (providerType === 'sftp') {
    if (field.name === 'authenticationType') return tx("Giriş yöntemi")
    if (field.name === 'remoteBaseDirectory') return tx("Yedeklerin saklanacağı klasör")
    if (field.name === 'hostKeyFingerprint') return tx("Sunucunun güvenlik parmak izi (SHA256)")
    if (field.name === 'host') return tx("Sunucu adresi")
  }
  if (providerType === 'local' && field.name === 'volume') return tx("Disk adı")
  return tx(field.label)
}

export function ConfigFieldInputs({ fields, values, onChange, invalidName, providerType }: { fields: readonly ConfigField[]; values: FieldValues; onChange: (name: string, value: string | boolean) => void; invalidName?: string; providerType?: string }) {
  useLanguage()
  const prefix = useId()
  const visibleFields = editableConfigFields(providerType, fields)
  return <div className="flex flex-wrap gap-x-3 gap-y-5">{visibleFields.map((field) => {
    const id = `${prefix}-${field.name}`
    const invalid = invalidName === field.name
    // The port sits small next to the server address; everything else takes a full row.
    const width = providerType === 'sftp' && field.name === 'host' ? 'min-w-48 flex-1' : providerType === 'sftp' && field.name === 'port' ? 'w-28' : 'w-full'
    if (field.kind === 'Boolean') {
      return <CheckField key={field.name} className={width} id={id} label={configLabel(field, providerType)} checked={values[field.name] === true} aria-invalid={invalid} onChange={(event) => onChange(field.name, event.target.checked)} />
    }
    if (providerType === 'sftp' && field.name === 'authenticationType') {
      return <div key={field.name} className={`space-y-1 text-sm ${width}`}>
        <label htmlFor={id} className="block font-medium text-text-heading">{configLabel(field, providerType)}</label>
        <Select id={id} value={String(values[field.name] ?? 'password')} aria-invalid={invalid} onChange={(event) => onChange(field.name, event.target.value)}>
          <option value="password">{tx("Parola")}</option>
          <option value="private-key">{tx("Özel anahtar")}</option>
        </Select>
      </div>
    }
    return <div key={field.name} className={`space-y-1 text-sm ${width}`}>
      <label htmlFor={id} className={`block font-medium text-text-heading${field.required ? ' required-mark' : ''}`}>{configLabel(field, providerType)}</label>
      <TextInput id={id} type={field.kind === 'Number' ? 'number' : 'text'} value={String(values[field.name] ?? '')} maxLength={field.kind === 'Text' ? field.maxLength ?? undefined : undefined}
        required={field.required} aria-invalid={invalid} onChange={(event) => onChange(field.name, event.target.value)} />
      {providerType === 'sftp' && field.name === 'hostKeyFingerprint' && <span className="block text-sm text-text-muted">{tx("Sunucu yöneticinizden parmak izini isteyin ya da ssh-keygen -lf çıktısını yapıştırın. Örnek: SHA256:AbCd…")}</span>}
      {providerType === 'local' && field.name === 'volume' && <span className="block text-sm text-text-muted">{tx("Yedekleme bilgisayarında tanımlı disk adı")}</span>}
    </div>
  })}</div>
}

/** Only newly entered values live in caller state. Stored credentials are never prefilled. */
export function SecretFieldInputs({ fields, values, onChange, invalidName, providerType, savedNames = [] }: { fields: readonly SecretField[]; values: Record<string, string>; onChange: (name: string, value: string) => void; invalidName?: string; providerType?: string; savedNames?: readonly string[] }) {
  useLanguage()
  const prefix = useId()
  return <>{fields.map((field) => <SecretInput key={field.name} id={`${prefix}-${field.name}`} label={tx(field.label)}
    multiline={providerType === 'sftp' && field.name === 'privateKey'} saved={savedNames.includes(field.name)} invalid={invalidName === field.name}
    hint={providerType === 'sftp' && field.name === 'passphrase' ? <span className="block">{tx("İsteğe bağlı; yalnızca şifreli özel anahtar için girin.")}</span> : undefined}
    required={field.required}
    value={values[field.name] ?? ''} onChange={(value) => onChange(field.name, value)} />)}</>
}
