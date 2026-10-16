import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { Field } from '@/components/ui/field'
import { FormDialog } from '@/features/platform/plans/form-dialog'
import { inputClass, selectClass } from '@/features/platform/styles'
import { errorMessage } from '@/lib/api/failure-messages'
import { useSetUploadBandwidth } from './hooks'
import type { BandwidthTarget } from './types'
import { bandwidthErrorMessage, bandwidthUnits, formatBandwidth, parseBandwidth, toInput } from './units'
import type { BandwidthUnitId } from './units'

const scopeHelp = {
  get tenant() { return tx("Tenant genelinde eş zamanlı tüm yüklemelerin toplamı için üst sınırdır; tüm Agent'lardaki yüklemeler bu sınırı adil paylaşır.") },
  get agent() { return tx("Bu Agent'ın yaptığı yüklemeler için üst sınırdır.") },
  get job() { return tx("Bu Backup Job'un tek bir çalıştırması için üst sınırdır. Yalnızca daha sıkı olabilir; Agent ve tenant sınırlarını aşamaz.") },
} as const

/** Mounted only while open (FormDialog), so every opening starts from the current value. */
export function LimitDialog({ target, name, current, minimum, maximum, onClose }: {
  target: BandwidthTarget; name: string; current: number | null; minimum: number; maximum: number; onClose: () => void
}) {
  useLanguage()
  const initial = current === null ? { text: '', unit: 'Mbit/s' as BandwidthUnitId } : toInput(current)
  const [unlimited, setUnlimited] = useState(current === null)
  const [text, setText] = useState(initial.text)
  const [unit, setUnit] = useState<BandwidthUnitId>(initial.unit)
  const save = useSetUploadBandwidth()
  const parsed = unlimited ? null : parseBandwidth(text, unit, minimum, maximum)
  const invalid = parsed !== null && 'error' in parsed
  const showInvalid = invalid && text.trim() !== ''
  function submit() {
    if (invalid) return
    save.mutate({ target, bytesPerSecond: parsed?.bytesPerSecond ?? null }, { onSuccess: onClose })
  }
  return <FormDialog title={tx("Yükleme hız sınırı: {0}", { "0": name })} description={scopeHelp[target.scope]} submitLabel="Kaydet" pendingLabel="Kaydediliyor…" pending={save.isPending} disabled={invalid}
    error={save.error ? errorMessage(save.error) : undefined} onSubmit={submit} onClose={onClose}>
    <label className="flex min-h-11 items-center gap-3 text-[0.9375rem] text-text">
      <input type="checkbox" className="size-5 accent-primary" checked={unlimited} onChange={event => setUnlimited(event.target.checked)} />{tx("Limitsiz (sınır yok)")}</label>
    <div className="grid gap-3 sm:grid-cols-[1fr_auto]">
      <Field label={tx("Hız sınırı")} error={showInvalid ? bandwidthErrorMessage(parsed.error, minimum, maximum) : undefined}>
        {control => <input {...control} className={inputClass} inputMode="decimal" value={text} disabled={unlimited} onChange={event => setText(event.target.value)} />}
      </Field>
      <Field label={tx("Birim")}>
        {control => <select {...control} className={`${selectClass} w-full`} value={unit} disabled={unlimited} onChange={event => setUnit(event.target.value as BandwidthUnitId)}>
          {bandwidthUnits.map(item => <option key={item.id} value={item.id}>{item.id}</option>)}
        </select>}
      </Field>
    </div>
    <p className="text-sm text-text-muted">{tx("1 Mbit/s = 125.000 B/s · 1 MB/s = 1.000.000 B/s. En az")}{' '}{formatBandwidth(minimum)}{tx(", en çok")}{' '}{formatBandwidth(maximum)}.</p>
  </FormDialog>
}
