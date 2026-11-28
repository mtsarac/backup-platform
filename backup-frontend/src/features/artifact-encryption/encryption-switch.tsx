import { tx, useLanguage } from '@/lib/i18n'
import { encryptionOn } from './labels'
import type { EncryptionPolicy } from './types'

/** One switch for "are backups encrypted". Turning it on sends Required, off sends Off; a stored Optional shows as on and stays Optional until the user flips the switch. */
export function EncryptionSwitch({ policy, onChange }: { policy: EncryptionPolicy; onChange: (policy: EncryptionPolicy) => void }) {
  useLanguage()
  const on = encryptionOn(policy)
  return <label className="flex min-h-control w-fit items-center gap-3 text-body text-text">
    <input type="checkbox" role="switch" className="peer sr-only" checked={on} onChange={event => onChange(event.target.checked ? 'Required' : 'Off')} />
    <span aria-hidden="true" className="relative h-6 w-11 shrink-0 rounded-full bg-input transition-colors after:absolute after:left-0.5 after:top-0.5 after:size-5 after:rounded-full after:bg-surface after:transition-transform peer-checked:bg-primary peer-checked:after:translate-x-5 peer-focus-visible:ring-2 peer-focus-visible:ring-ring peer-focus-visible:ring-offset-2" />
    {on ? tx('Şifreleme: Açık') : tx('Şifreleme: Kapalı')}
  </label>
}
