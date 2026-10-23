import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { Field } from '@/components/ui/field'
import { FormDialog } from '@/features/platform/plans/form-dialog'
import { inputClass, selectClass } from '@/features/platform/styles'
import { errorMessage } from '@/lib/api/failure-messages'
import { useSaveBandwidthProfile } from './hooks'
import type { BandwidthProfile, TransferScheduleWindowDto } from './types'
import { bandwidthUnits, formatBandwidth, parseBandwidth } from './units'
import type { BandwidthUnitId } from './units'

const DAYS = [
  { value: 1, label: 'Pazartesi' },
  { value: 2, label: 'Salı' },
  { value: 3, label: 'Çarşamba' },
  { value: 4, label: 'Perşembe' },
  { value: 5, label: 'Cuma' },
  { value: 6, label: 'Cumartesi' },
  { value: 0, label: 'Pazar' },
]

export function ProfileDialog({ profile, minimum, maximum, onClose }: {
  profile?: BandwidthProfile | null; minimum: number; maximum: number; onClose: () => void
}) {
  useLanguage()
  const [name, setName] = useState(profile?.name ?? '')
  const [timeZoneId, setTimeZoneId] = useState(profile?.timeZoneId ?? 'Europe/Istanbul')
  const [windows, setWindows] = useState<TransferScheduleWindowDto[]>(profile?.windows ? [...profile.windows] : [])
  const save = useSaveBandwidthProfile()

  // New window draft
  const [day, setDay] = useState(1)
  const [startTime, setStartTime] = useState('09:00')
  const [endTime, setEndTime] = useState('18:00')
  const [rateText, setRateText] = useState('10')
  const [rateUnit, setRateUnit] = useState<BandwidthUnitId>('Mbit/s')
  const [pause, setPause] = useState(false)

  const parsed = pause ? { bytesPerSecond: 0 } : parseBandwidth(rateText, rateUnit, minimum, maximum)
  const isRateValid = parsed !== null && !('error' in parsed)

  function addWindow() {
    if (!isRateValid || !startTime || !endTime || startTime === endTime) return
    const newWindow: TransferScheduleWindowDto = {
      dayOfWeek: day,
      startTime: startTime.length === 5 ? `${startTime}:00` : startTime,
      endTime: endTime.length === 5 ? `${endTime}:00` : endTime,
      bytesPerSecond: parsed.bytesPerSecond
    }
    setWindows([...windows, newWindow])
  }

  function removeWindow(index: number) {
    setWindows(windows.filter((_, i) => i !== index))
  }

  function submit() {
    if (!name.trim()) return
    save.mutate({ id: profile?.id, name: name.trim(), timeZoneId: timeZoneId.trim(), windows }, { onSuccess: onClose })
  }

  return (
    <FormDialog
      title={profile ? tx("Profili Düzenle: {0}", { "0": profile.name }) : tx("Yeni Bant Genişliği Profili")}
      description={tx("Profil statik sınırların yerine geçer; pencere dışında sınırsızdır. Duraklatma penceresi aktarımı saklar; devam ettirme manueldir.")}
      submitLabel={tx("Kaydet")}
      pendingLabel={tx("Kaydediliyor…")}
      pending={save.isPending}
      disabled={!name.trim()}
      error={save.error ? errorMessage(save.error) : undefined}
      onSubmit={submit}
      onClose={onClose}
    >
      <div className="space-y-4">
        <Field label={tx("Profil Adı")}>
          {control => <input {...control} className={inputClass} value={name} onChange={e => setName(e.target.value)} placeholder={tx("Örn. Mesai Saatleri")} maxLength={200} required />}
        </Field>
        <Field label={tx("Zaman Dilimi (IANA)")}>
          {control => <input {...control} className={inputClass} value={timeZoneId} onChange={e => setTimeZoneId(e.target.value)} placeholder="Europe/Istanbul" required />}
        </Field>

        <div className="rounded-lg border border-border p-3 space-y-3 bg-surface-muted/30">
          <h4 className="text-sm font-semibold text-text-heading">{tx("Pencere Ekle")}</h4>
          <Field label={tx("Pencere Davranışı")}>
            {control => <select {...control} className={selectClass} value={pause ? 'pause' : 'limit'} onChange={e => setPause(e.target.value === 'pause')}>
              <option value="limit">{tx("Hız Limiti")}</option>
              <option value="pause">{tx("Aktarımı Duraklat")}</option>
            </select>}
          </Field>
          <div className="grid grid-cols-1 gap-2 sm:grid-cols-2">
            <Field label={tx("Gün")}>
              {control => (
                <select {...control} className={selectClass} value={day} onChange={e => setDay(Number(e.target.value))}>
                  {DAYS.map(d => <option key={d.value} value={d.value}>{tx(d.label)}</option>)}
                </select>
              )}
            </Field>
            <Field label={tx("Başlangıç")}>
              {control => <input {...control} type="time" className={inputClass} value={startTime} onChange={e => setStartTime(e.target.value)} />}
            </Field>
            <Field label={tx("Bitiş")}>
              {control => <input {...control} type="time" className={inputClass} value={endTime} onChange={e => setEndTime(e.target.value)} />}
            </Field>
            <Field label={tx("Hız Limiti")}>
              {control => (
                <div className="flex gap-1">
                  <input {...control} className={inputClass} value={rateText} disabled={pause} onChange={e => setRateText(e.target.value)} />
                  <select aria-label={tx("Hız Birimi")} className={selectClass} value={rateUnit} disabled={pause} onChange={e => setRateUnit(e.target.value as BandwidthUnitId)}>
                    {bandwidthUnits.map(u => <option key={u.id} value={u.id}>{u.id}</option>)}
                  </select>
                </div>
              )}
            </Field>
          </div>
          <button type="button" onClick={addWindow} disabled={!isRateValid || !startTime || !endTime || startTime === endTime} className="text-xs font-medium text-primary hover:underline">
            + {tx("Pencereyi Listeye Ekle")}
          </button>
        </div>

        {windows.length > 0 && (
          <div className="space-y-2">
            <h4 className="text-sm font-semibold text-text-heading">{tx("Tanımlı Pencereler ({0})", { "0": windows.length })}</h4>
            <div className="max-h-40 overflow-y-auto space-y-1">
              {windows.map((w, idx) => (
                <div key={idx} className="flex items-center justify-between rounded border border-border px-3 py-1.5 text-xs">
                  <span>
                    <strong>{tx(DAYS.find(d => d.value === w.dayOfWeek)?.label ?? '')}</strong>: {w.startTime.slice(0, 5)} - {w.endTime.slice(0, 5)} &rarr; {w.bytesPerSecond === 0 ? tx("Aktarımı Duraklat") : formatBandwidth(w.bytesPerSecond)}
                  </span>
                  <button type="button" onClick={() => removeWindow(idx)} className="text-error hover:underline">{tx("Kaldır")}</button>
                </div>
              ))}
            </div>
          </div>
        )}
      </div>
    </FormDialog>
  )
}
