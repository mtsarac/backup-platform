import { tx } from '@/lib/i18n'
import { Field } from '@/components/ui/field'
import { inputClass } from '@/features/platform/styles'
import type { DatabaseLogin } from '@/features/backup-jobs/types'

interface Props {
  idPrefix: string
  login: DatabaseLogin | null | undefined
  onChange: (login: DatabaseLogin | null) => void
  /** A login is already stored for this target: leaving both fields empty keeps it. */
  hasStored?: boolean
  disabled?: boolean
  errors?: { userName?: string; password?: string }
}

/** The database user name and password typed for one target; the password is write-only and never read back from the API. */
export function DatabaseLoginFields({ idPrefix, login, onChange, hasStored = false, disabled = false, errors }: Props) {
  const userName = login?.userName ?? ''
  const password = login?.password ?? ''
  const change = (next: DatabaseLogin) => onChange(next.userName === '' && next.password === '' ? null : next)
  return <>
    <Field id={`${idPrefix}-user`} label={tx("Veritabanı kullanıcı adı")} error={errors?.userName}>
      {control => <input {...control} className={inputClass} autoComplete="off" value={userName} disabled={disabled} onChange={event => change({ userName: event.target.value, password })} />}
    </Field>
    <Field id={`${idPrefix}-password`} label={tx("Veritabanı parolası")} hint={hasStored ? tx("Kayıtlı bir giriş var. Değiştirmek için yeni kullanıcı adı ve parola girin; boş bırakırsanız mevcut giriş korunur.") : undefined} error={errors?.password}>
      {control => <input {...control} type="password" className={inputClass} autoComplete="new-password" value={password} disabled={disabled} onChange={event => change({ userName, password: event.target.value })} />}
    </Field>
  </>
}
