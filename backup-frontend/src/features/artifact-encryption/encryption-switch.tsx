import { tx, useLanguage } from '@/lib/i18n'
import { encryptionOn } from './labels'
import type { EncryptionPolicy } from './types'

/** One switch for "are backups encrypted". Turning it on sends Required, off sends Off; a stored Optional shows as on and stays Optional until the user flips the switch. */
export function EncryptionSwitch({ policy, onChange }: { policy: EncryptionPolicy; onChange: (policy: EncryptionPolicy) => void }) {
  useLanguage()
  const on = encryptionOn(policy)
  return <label className="flex min-h-control w-fit items-center gap-3 text-body text-text">
    <input type="checkbox" role="switch" className="size-5 shrink-0 accent-primary" checked={on} onChange={event => onChange(event.target.checked ? 'Required' : 'Off')} />
    {on ? tx('Şifreleme: Açık') : tx('Şifreleme: Kapalı')}
  </label>
}
