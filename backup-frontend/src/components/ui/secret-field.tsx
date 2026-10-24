import { tx, useLanguage } from '@/lib/i18n'
import { Check } from 'lucide-react'
import type { ReactNode } from 'react'
import { Field } from '@/components/ui/field'
import { inputClass } from '@/components/ui/form-styles'

/**
 * The one control for a write-only secret. It is a password input (a multi-line one only for PEM keys), has no reveal button, and
 * never receives the stored value: `saved` only says that one exists, shown as "Kayıtlı". Callers empty it on submit (`useSecretValue`).
 */
export function SecretField({ label, value, onChange, saved = false, hint, error, required, disabled, invalid, multiline = false, className = inputClass, id }: {
  label: ReactNode; value: string; onChange: (value: string) => void; saved?: boolean; hint?: ReactNode; error?: ReactNode
  required?: boolean; disabled?: boolean; invalid?: boolean; multiline?: boolean; className?: string; id?: string
}) {
  useLanguage()
  return <Field id={id} label={label} hint={hint} error={error} required={required}>
    {field => {
      const control = { ...field, 'aria-invalid': field['aria-invalid'] || invalid || undefined }
      return <>
        {multiline
          ? <textarea {...control} className={`${className} h-auto font-mono`} rows={6} autoComplete="off" spellCheck={false} autoCapitalize="none" required={required} disabled={disabled} value={value} onChange={event => onChange(event.target.value)} />
          : <input {...control} className={className} type="password" autoComplete="new-password" required={required} disabled={disabled} value={value} onChange={event => onChange(event.target.value)} />}
        {saved && !value && <p className="flex items-center gap-1 text-sm text-success"><Check aria-hidden="true" className="size-4" /><span className="font-medium">{tx("Kayıtlı")}</span><span className="text-text-muted">{tx("· Değer bir daha gösterilmez; değiştirmek için yenisini yazın.")}</span></p>}
      </>
    }}
  </Field>
}
