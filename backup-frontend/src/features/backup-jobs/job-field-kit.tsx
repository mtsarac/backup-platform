import { tx } from '@/lib/i18n'
import { Field } from '@/components/ui/field'
import { inputClass, selectClass } from '@/components/ui/form-styles'

export type FieldErrors = Record<string, string>

/** Field renderers bound to one error map; they return elements, not components, so inputs never remount. */
export function fieldKit(errors: FieldErrors) {
  const error = (name: string) => errors[name] ?? errors[name.replace(/^schedule\./, '')] ?? errors[name.split('.').at(-1) ?? name]
  return {
    text: (name: string, label: string, value: string, onChange: (value: string) => void, disabled = false) =>
      <Field id={name} label={label} error={error(name)}>{control => <input {...control} className={inputClass} value={value} disabled={disabled} onChange={event => onChange(event.target.value)} />}</Field>,
    number: (name: string, label: string, value: number | null, onChange: (value: number | null) => void, disabled = false) =>
      <Field id={name} label={label} error={error(name)}>{control => <input {...control} type="number" step="any" className={inputClass} value={value ?? ''} disabled={disabled} onChange={event => onChange(event.target.value === '' ? null : Number(event.target.value))} />}</Field>,
    check: (label: string, value: boolean, onChange: (value: boolean) => void, disabled = false) =>
      <label className="flex min-h-11 items-center gap-3 text-[0.9375rem] text-text"><input type="checkbox" className="size-5 shrink-0 accent-primary" checked={value} disabled={disabled} onChange={event => onChange(event.target.checked)} />{tx(label)}</label>,
    select: (name: string, label: string, value: string, options: readonly (readonly [string, string])[], onChange: (value: string) => void) =>
      <Field id={name} label={label} error={error(name)}>{control => <select {...control} className={`${selectClass} w-full min-w-0`} value={value} onChange={event => onChange(event.target.value)}>{options.map(([key, text]) => <option key={key} value={key}>{text}</option>)}</select>}</Field>,
  }
}
