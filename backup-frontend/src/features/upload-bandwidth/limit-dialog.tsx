import { useState } from 'react'
import { FormDialog } from '@/features/platform/plans/form-dialog'
import { inputClass, selectClass } from '@/features/platform/styles'
import { errorMessage } from '@/lib/api/failure-messages'
import { useSetUploadBandwidth } from './hooks'
import type { BandwidthTarget } from './types'
import { bandwidthErrorMessage, bandwidthUnits, formatBandwidth, parseBandwidth, toInput, type BandwidthUnitId } from './units'

const scopeHelp = {
  tenant: 'Tenant genelinde eş zamanlı tüm yüklemelerin toplamı için üst sınırdır; tüm Agent\'lardaki yüklemeler bu sınırı adil paylaşır.',
  agent: 'Bu Agent\'ın yaptığı yüklemeler için üst sınırdır.',
  job: 'Bu Backup Job\'un tek bir çalıştırması için üst sınırdır. Yalnızca daha sıkı olabilir; Agent ve tenant sınırlarını aşamaz.',
} as const

/** Mounted only while open (FormDialog), so every opening starts from the current value. */
export function LimitDialog({ target, name, current, minimum, maximum, onClose }: {
  target: BandwidthTarget; name: string; current: number | null; minimum: number; maximum: number; onClose: () => void
}) {
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
  return <FormDialog title={`Yükleme hız sınırı: ${name}`} description={scopeHelp[target.scope]} submitLabel="Kaydet" pendingLabel="Kaydediliyor…" pending={save.isPending} disabled={invalid}
    error={save.error ? errorMessage(save.error) : undefined} onSubmit={submit} onClose={onClose}>
    <label className="flex items-center gap-2 text-sm text-text">
      <input type="checkbox" checked={unlimited} onChange={event => setUnlimited(event.target.checked)} />
      Limitsiz (sınır yok)
    </label>
    <div className="grid grid-cols-[1fr_auto] gap-3">
      <label className="block text-sm text-text-muted">Hız sınırı
        <input className={inputClass} inputMode="decimal" value={text} disabled={unlimited} aria-invalid={showInvalid} aria-describedby="bandwidth-hint" onChange={event => setText(event.target.value)} />
      </label>
      <label className="block text-sm text-text-muted">Birim
        <select className={selectClass} value={unit} disabled={unlimited} onChange={event => setUnit(event.target.value as BandwidthUnitId)}>
          {bandwidthUnits.map(item => <option key={item.id} value={item.id}>{item.id}</option>)}
        </select>
      </label>
    </div>
    <p id="bandwidth-hint" className={`text-xs ${showInvalid ? 'text-error' : 'text-text-muted'}`}>
      {showInvalid ? bandwidthErrorMessage(parsed.error, minimum, maximum) : `1 Mbit/s = 125.000 B/s · 1 MB/s = 1.000.000 B/s. En az ${formatBandwidth(minimum)}, en çok ${formatBandwidth(maximum)}.`}
    </p>
  </FormDialog>
}
