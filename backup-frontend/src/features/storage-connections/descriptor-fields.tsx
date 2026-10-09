import { useId } from 'react'
import { inputClass } from '@/features/platform/styles'
import type { FieldValues } from './descriptor-values'
import type { ConfigField, SecretField } from './types'

export function ConfigFieldInputs({ fields, values, onChange, invalidName }: { fields: readonly ConfigField[]; values: FieldValues; onChange: (name: string, value: string | boolean) => void; invalidName?: string }) {
  const prefix = useId()
  return <>{fields.map((field) => {
    const id = `${prefix}-${field.name}`
    const invalid = invalidName === field.name
    if (field.kind === 'Boolean') {
      return <label key={field.name} htmlFor={id} className="flex items-center gap-2 text-sm text-text">
        <input id={id} type="checkbox" checked={values[field.name] === true} aria-invalid={invalid} onChange={(event) => onChange(field.name, event.target.checked)} />{field.label}
      </label>
    }
    return <div key={field.name} className="text-sm text-text-muted">
      <label htmlFor={id}>{field.label}</label>
      <input id={id} className={inputClass} type={field.kind === 'Number' ? 'number' : 'text'} value={String(values[field.name] ?? '')} maxLength={field.kind === 'Text' ? field.maxLength ?? undefined : undefined}
        required={field.required} aria-invalid={invalid} onChange={(event) => onChange(field.name, event.target.value)} />
      {field.required && <span className="mt-1 block text-xs">Zorunlu alan.</span>}
    </div>
  })}</>
}

/** Password inputs only: values live in the caller's state until submit, are never prefilled and never rendered back. */
export function SecretFieldInputs({ fields, values, onChange, invalidName }: { fields: readonly SecretField[]; values: Record<string, string>; onChange: (name: string, value: string) => void; invalidName?: string }) {
  const prefix = useId()
  return <>{fields.map((field) => {
    const id = `${prefix}-${field.name}`
    return <div key={field.name} className="text-sm text-text-muted">
      <label htmlFor={id}>{field.label}</label>
      <input id={id} className={inputClass} type="password" autoComplete="new-password" value={values[field.name] ?? ''} aria-invalid={invalidName === field.name}
        onChange={(event) => onChange(field.name, event.target.value)} />
      {field.required && <span className="mt-1 block text-xs">Zorunlu alan.</span>}
    </div>
  })}</>
}
