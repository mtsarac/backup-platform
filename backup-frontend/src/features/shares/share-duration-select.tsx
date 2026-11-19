import { Field } from '@/components/ui/field'
import { Select } from '@/components/ui/text-input'
import { tx, useLanguage } from '@/lib/i18n'

const label = (hours: number) => hours === 1 ? tx('1 saat') : hours === 24 ? tx('24 saat') : tx('3 gün')

/** The only way to pick a share duration: 1 saat / 24 saat / 3 gün, no free number. */
export function ShareDurationSelect({ label: fieldLabel, hint, hours, value, onChange }: { label: string; hint?: string; hours: readonly number[]; value: number; onChange: (hours: number) => void }) {
  useLanguage()
  return <Field label={fieldLabel} hint={hint}>
    {(fieldProps) => <Select {...fieldProps} value={value} onChange={(event) => onChange(Number(event.target.value))}>
      {hours.map((option) => <option key={option} value={option}>{label(option)}</option>)}
    </Select>}
  </Field>
}
