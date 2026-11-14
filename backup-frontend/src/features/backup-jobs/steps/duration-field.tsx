import { useState } from 'react'
import { Field } from '@/components/ui/field'
import { Select, TextInput } from '@/components/ui/text-input'
import { tx } from '@/lib/i18n'

const units = [86400, 3600, 60, 1] as const

export function durationUnit(seconds: number) {
  return units.find(unit => seconds !== 0 && seconds % unit === 0) ?? 1
}

export function DurationField({ name, label, value, onChange, disabled, error }: {
  name: string; label: string; value: number; onChange: (value: number) => void; disabled?: boolean; error?: string
}) {
  const [unit, setUnit] = useState<number>(() => durationUnit(value))
  return <Field id={name} label={label} error={error}>{control => <div className="flex gap-2">
    <TextInput {...control} type="number" step="any" value={value / unit} disabled={disabled} onChange={event => onChange(event.target.value === '' ? 0 : Number(event.target.value) * unit)} />
    <Select className="w-auto" aria-label={`${label} — ${tx('Süre birimi')}`} value={unit} disabled={disabled} onChange={event => setUnit(Number(event.target.value))}>
      <option value={1}>{tx('Saniye')}</option>
      <option value={60}>{tx('Dakika')}</option>
      <option value={3600}>{tx('Saat')}</option>
      <option value={86400}>{tx('Gün')}</option>
    </Select>
  </div>}</Field>
}
