import { useId, type ReactNode } from 'react'

/** Label, hint and error bound to one control. `children` receives the ids to put on the control. */
export function Field({ id: suppliedId, label, hint, error, required, children }: {
  id?: string; label: ReactNode; hint?: ReactNode; error?: ReactNode; required?: boolean
  children: (control: { id: string; 'aria-describedby'?: string; 'aria-invalid'?: boolean }) => ReactNode
}) {
  const generatedId = useId()
  const id = suppliedId ?? generatedId
  const describedBy = [hint ? `${id}-hint` : '', error ? `${id}-error` : ''].filter(Boolean).join(' ') || undefined
  return <div className="space-y-1">
    <label htmlFor={id} className="block text-sm font-medium text-text-heading">{label}{required && <span aria-hidden="true" className="text-error"> *</span>}</label>
    {children({ id, 'aria-describedby': describedBy, 'aria-invalid': error ? true : undefined })}
    {hint && <p id={`${id}-hint`} className="text-sm text-text-muted">{hint}</p>}
    {error && <p id={`${id}-error`} role="alert" className="text-sm text-error">{error}</p>}
  </div>
}
