import { tx } from '@/lib/i18n'
import { CheckField } from '@/components/ui/check-field'
import { Field } from '@/components/ui/field'
import { inputClass, selectClass } from '@/components/ui/form-styles'

export type FieldErrors = Record<string, string>

/** Field renderers bound to one error map; they return elements, not components, so inputs never remount. */
export function fieldKit(errors: FieldErrors) {
  const error = (name: string) => errors[name] ?? errors[name.replace(/^schedule\./, '')] ?? errors[name.split('.').at(-1) ?? name]
  return {
    withErrors: (...names: string[]) => names.some(item => errors[item]),
    text: (name: string, label: string, value: string, onChange: (value: string) => void, disabled = false) =>
      <Field id={name} label={label} error={error(name)}>{control => <input {...control} className={inputClass} value={value} disabled={disabled} onChange={event => onChange(event.target.value)} />}</Field>,
    number: (name: string, label: string, value: number | null, onChange: (value: number | null) => void, disabled = false) =>
      <Field id={name} label={label} error={error(name)}>{control => <input {...control} type="number" step="any" className={inputClass} value={value ?? ''} disabled={disabled} onChange={event => onChange(event.target.value === '' ? null : Number(event.target.value))} />}</Field>,
    check: (label: string, value: boolean, onChange: (value: boolean) => void, disabled = false) =>
      <CheckField label={tx(label)} checked={value} disabled={disabled} onChange={event => onChange(event.target.checked)} />,
    select: (name: string, label: string, value: string, options: readonly (readonly [string, string])[], onChange: (value: string) => void) =>
      <Field id={name} label={label} error={error(name)}>{control => <select {...control} className={`${selectClass} w-full min-w-0`} value={value} onChange={event => onChange(event.target.value)}>{options.map(([key, text]) => <option key={key} value={key}>{text}</option>)}</select>}</Field>,
  }
}
