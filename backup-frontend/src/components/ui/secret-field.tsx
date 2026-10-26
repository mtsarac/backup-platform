import { tx, useLanguage } from '@/lib/i18n'
import { Check } from 'lucide-react'
import type { ReactNode } from 'react'
import { Field } from '@/components/ui/field'
import { inputClass } from '@/components/ui/form-styles'
import { Button } from '@/components/ui/button'

/**
 * The one control for a write-only secret: never reveal or prefill a stored value. Callers empty it on submit (`useSecretValue`).
 * Multiline keys are pasted whole so password inputs cannot silently strip their line breaks.
 */
export function SecretField({ label, value, onChange, saved = false, hint, error, required, disabled, invalid, multiline = false, className = inputClass, id }: {
  label: ReactNode; value: string; onChange: (value: string) => void; saved?: boolean; hint?: ReactNode; error?: ReactNode
  required?: boolean; disabled?: boolean; invalid?: boolean; multiline?: boolean; className?: string; id?: string
}) {
  useLanguage()
  return <Field id={id} label={label} hint={multiline ? <>{hint}<span className="block">{tx("Çok satırlı anahtarın tamamını yapıştırın. Değiştirmek için yeniden yapıştırın veya girişi temizleyin.")}</span></> : hint} error={error} required={required}>
    {field => {
      const control = { ...field, 'aria-invalid': field['aria-invalid'] || invalid || undefined }
      return <>
        {multiline
          ? <div className="flex flex-wrap gap-2">
            <input {...control} className={className} type="password" autoComplete="off" readOnly required={required} disabled={disabled} value={value ? '********' : ''}
              onPaste={event => { event.preventDefault(); if (!disabled) onChange(event.clipboardData.getData('text')) }}
              onKeyDown={event => { if (!disabled && (event.key === 'Backspace' || event.key === 'Delete')) { event.preventDefault(); onChange('') } }} />
            <Button type="button" variant="outline" size="sm" disabled={disabled || !value} onClick={() => onChange('')}>{tx("Girişi temizle")}</Button>
          </div>
          : <input {...control} className={className} type="password" autoComplete="new-password" required={required} disabled={disabled} value={value} onChange={event => onChange(event.target.value)} />}
        {saved && !value && <p className="flex items-center gap-1 text-sm text-success"><Check aria-hidden="true" className="size-4" /><span className="font-medium">{tx("Kayıtlı")}</span><span className="text-text-muted">{tx("· Değer bir daha gösterilmez; değiştirmek için yenisini yazın.")}</span></p>}
      </>
    }}
  </Field>
}
