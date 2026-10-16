import { tx, useLanguage } from '@/lib/i18n'
import { useId } from 'react'
import { inputClass, selectClass } from '@/features/platform/styles'
import type { FieldValues } from './descriptor-values'
import type { ConfigField, SecretField } from './types'

export function ConfigFieldInputs({ fields, values, onChange, invalidName, providerType }: { fields: readonly ConfigField[]; values: FieldValues; onChange: (name: string, value: string | boolean) => void; invalidName?: string; providerType?: string }) {
  useLanguage()
  const prefix = useId()
  const visibleFields = providerType === 'google-drive'
    ? fields.filter((f) => !['accountEmail', 'accountName', 'folderId', 'reauthorizationRequired'].includes(f.name))
    : fields
  return <>{visibleFields.map((field) => {
    const id = `${prefix}-${field.name}`
    const invalid = invalidName === field.name
    if (field.kind === 'Boolean') {
      return <label key={field.name} htmlFor={id} className="flex items-center gap-2 text-sm text-text">
        <input id={id} className="size-5 accent-primary" type="checkbox" checked={values[field.name] === true} aria-invalid={invalid} onChange={(event) => onChange(field.name, event.target.checked)} />{tx(field.label)}
      </label>
    }
    if (providerType === 'sftp' && field.name === 'authenticationType') {
      return <div key={field.name} className="space-y-1 text-sm">
        <label htmlFor={id} className="block font-medium text-text-heading">{tx(field.label)}</label>
        <select id={id} className={`${selectClass} mt-0 w-full`} value={String(values[field.name] ?? 'password')} aria-invalid={invalid} onChange={(event) => onChange(field.name, event.target.value)}>
          <option value="password">{tx("Parola")}</option>
          <option value="private-key">{tx("Özel anahtar")}</option>
        </select>
      </div>
    }
    return <div key={field.name} className="space-y-1 text-sm">
      <label htmlFor={id} className="block font-medium text-text-heading">{tx(field.label)}</label>
      <input id={id} className={`${inputClass} mt-0`} type={field.kind === 'Number' ? 'number' : 'text'} value={String(values[field.name] ?? '')} maxLength={field.kind === 'Text' ? field.maxLength ?? undefined : undefined}
        required={field.required} aria-invalid={invalid} onChange={(event) => onChange(field.name, event.target.value)} />
      {field.required && <span className="block text-sm text-text-muted">{tx("Zorunlu alan.")}</span>}
      {providerType === 'sftp' && field.name === 'hostKeyFingerprint' && <span className="block text-sm text-text-muted">{tx("SHA256 sunucu anahtarı parmak izini güvenilir bir kanaldan alın ve doğrulayarak girin. Otomatik keşif veya ilk bağlantıda güven yoktur; uyuşmazlık bağlantıyı engeller.")}</span>}
    </div>
  })}</>
}

/** Only newly entered values live in caller state. Stored credentials are never prefilled. */
export function SecretFieldInputs({ fields, values, onChange, invalidName, providerType }: { fields: readonly SecretField[]; values: Record<string, string>; onChange: (name: string, value: string) => void; invalidName?: string; providerType?: string }) {
  useLanguage()
  const prefix = useId()
  return <>{fields.map((field) => {
    const id = `${prefix}-${field.name}`
    return <div key={field.name} className="space-y-1 text-sm">
      <label htmlFor={id} className="block font-medium text-text-heading">{tx(field.label)}</label>
      {providerType === 'sftp' && field.name === 'privateKey'
        ? <textarea id={id} className={`${inputClass} mt-0 h-auto font-mono`} rows={6} autoComplete="off" spellCheck={false} autoCapitalize="none" value={values[field.name] ?? ''} aria-invalid={invalidName === field.name} onChange={(event) => onChange(field.name, event.target.value)} />
        : <input id={id} className={`${inputClass} mt-0`} type="password" autoComplete="new-password" value={values[field.name] ?? ''} aria-invalid={invalidName === field.name}
          onChange={(event) => onChange(field.name, event.target.value)} />}
      {providerType === 'sftp' && field.name === 'passphrase' && <span className="block text-sm text-text-muted">{tx("İsteğe bağlı; yalnızca şifreli özel anahtar için girin.")}</span>}
      {field.required && <span className="block text-sm text-text-muted">{tx("Zorunlu alan.")}</span>}
    </div>
  })}</>
}
