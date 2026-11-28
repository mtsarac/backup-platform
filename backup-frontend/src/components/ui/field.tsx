import { tx, useLanguage } from '@/lib/i18n'
import { useId, type ReactNode } from 'react'
import { cn } from '@/lib/utils'

/** The one required-field marker is a CSS asterisk (`required-mark`), so the label text stays the plain name. A label that is not a `Field` wraps its text in this. */
export function RequiredText({ children }: { children: ReactNode }) {
  return <span className="required-mark">{children}</span>
}

/** Label, hint and error bound to one control. `children` receives the ids to put on the control. */
export function Field({ id: suppliedId, label, hint, error, required, children }: {
  id?: string; label: ReactNode; hint?: ReactNode; error?: ReactNode; required?: boolean
  children: (control: { id: string; 'aria-describedby'?: string; 'aria-invalid'?: boolean }) => ReactNode
}) {
  useLanguage()
  const generatedId = useId()
  const id = suppliedId ?? generatedId
  const describedBy = [hint ? `${id}-hint` : '', error ? `${id}-error` : ''].filter(Boolean).join(' ') || undefined
  return <div className="space-y-1">
    <label htmlFor={id} className={cn('block text-sm font-medium text-text-heading', required && 'required-mark')}>{tx(label)}</label>
    {children({ id, 'aria-describedby': describedBy, 'aria-invalid': error ? true : undefined })}
    {hint && <p id={`${id}-hint`} className="text-sm text-text-muted">{hint}</p>}
    {error && <p id={`${id}-error`} role="alert" className="text-sm text-error">{tx(error)}</p>}
  </div>
}
