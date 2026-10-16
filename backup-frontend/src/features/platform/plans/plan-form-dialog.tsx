import { useState } from 'react'
import { Field } from '@/components/ui/field'
import { ApiError } from '@/lib/api/client'
import { errorMessage } from '@/lib/api/failure-messages'
import { inputClass } from '../styles'
import { FormDialog } from './form-dialog'
import { useCreatePlan, useUpdatePlan } from './hooks'
import { gbErrorMessage, parseGb, toGbInput } from './storage'
import type { Plan } from './types'

type Size = { bytes: number } | { unlimited: true }

function CommonFields({ name, setName, unlimited, setUnlimited, sizeText, setSizeText, sizeError, nameInvalid, sizeInvalid }: {
  name: string; setName: (value: string) => void; unlimited: boolean; setUnlimited: (value: boolean) => void
  sizeText: string; setSizeText: (value: string) => void; sizeError: string; nameInvalid: boolean; sizeInvalid: boolean
}) {
  return <>
    <Field label="Plan adı">{(control) => <input {...control} className={inputClass} value={name} maxLength={100} required aria-invalid={nameInvalid} onChange={(event) => setName(event.target.value)} />}</Field>
    <label className="flex min-h-8 items-center gap-2 text-sm text-text"><input className="size-4" type="checkbox" checked={unlimited} onChange={(event) => setUnlimited(event.target.checked)} />Limitsiz</label>
    {!unlimited && <Field label="Depolama limiti (GB)" hint="1 GB = 1.000.000.000 bayt (ondalık). En fazla 3 ondalık basamak." error={sizeError}>
      {(control) => <input {...control} className={inputClass} inputMode="decimal" value={sizeText} required aria-invalid={sizeInvalid || Boolean(sizeError)} onChange={(event) => setSizeText(event.target.value)} />}
    </Field>}
  </>
}

function resolveSize(unlimited: boolean, sizeText: string, unchanged: Size | null, initialText: string): { size: Size } | { error: string } {
  if (unlimited) return { size: { unlimited: true } }
  if (unchanged && sizeText === initialText) return { size: unchanged }
  const parsed = parseGb(sizeText)
  return 'error' in parsed ? { error: gbErrorMessage(parsed.error) } : { size: { bytes: parsed.bytes } }
}

export function CreatePlanDialog({ onClose }: { onClose: () => void }) {
  const [name, setName] = useState('')
  const [unlimited, setUnlimited] = useState(false)
  const [sizeText, setSizeText] = useState('')
  const [enabled, setEnabled] = useState(true)
  const [sizeError, setSizeError] = useState('')
  const create = useCreatePlan()
  function submit() {
    const resolved = resolveSize(unlimited, sizeText, null, '')
    if ('error' in resolved) { setSizeError(resolved.error); return }
    setSizeError('')
    const limit = 'unlimited' in resolved.size ? { unlimited: true } : { storageLimitBytes: resolved.size.bytes }
    create.mutate({ name: name.trim(), ...limit, enabled }, { onSuccess: onClose })
  }
  const field = create.error instanceof ApiError ? create.error.field : undefined
  return <FormDialog title="Yeni plan" description="Plan, tenant abonelikleri için depolama limitini belirler. Oluşturduktan sonra tenant'a atayın." submitLabel="Plan oluştur" pendingLabel="Oluşturuluyor…" pending={create.isPending} disabled={!name.trim()} error={create.error ? errorMessage(create.error) : undefined} onSubmit={submit} onClose={onClose}>
    <CommonFields name={name} setName={setName} unlimited={unlimited} setUnlimited={setUnlimited} sizeText={sizeText} setSizeText={setSizeText} sizeError={sizeError} nameInvalid={field === 'name'} sizeInvalid={field === 'storageLimit'} />
    <label className="flex items-center gap-2 text-sm text-text"><input type="checkbox" checked={enabled} onChange={(event) => setEnabled(event.target.checked)} />Etkin (yeni atamalarda seçilebilir)</label>
  </FormDialog>
}

export function EditPlanDialog({ plan, onClose }: { plan: Plan; onClose: () => void }) {
  const initialText = plan.storageLimitBytes === null ? '' : toGbInput(plan.storageLimitBytes)
  const [name, setName] = useState(plan.name)
  const [unlimited, setUnlimited] = useState(plan.storageLimitBytes === null)
  const [sizeText, setSizeText] = useState(initialText)
  const [sizeError, setSizeError] = useState('')
  const update = useUpdatePlan(plan.planId)
  function submit() {
    const unchanged: Size | null = plan.storageLimitBytes === null ? null : { bytes: plan.storageLimitBytes }
    const resolved = resolveSize(unlimited, sizeText, unchanged, initialText)
    if ('error' in resolved) { setSizeError(resolved.error); return }
    setSizeError('')
    const limit = 'unlimited' in resolved.size ? { unlimited: true } : { storageLimitBytes: resolved.size.bytes }
    update.mutate({ name: name.trim(), ...limit, ...(plan.quotaPolicyReference ? { quotaPolicyReference: plan.quotaPolicyReference } : {}) }, { onSuccess: onClose })
  }
  const field = update.error instanceof ApiError ? update.error.field : undefined
  return <FormDialog title={`Planı düzenle: ${plan.name}`} description="Ad ve limit değişikliği bu planı kullanan aboneliklere de yansır." submitLabel="Kaydet" pendingLabel="Kaydediliyor…" pending={update.isPending} disabled={!name.trim()} error={update.error ? errorMessage(update.error) : undefined} onSubmit={submit} onClose={onClose}>
    <CommonFields name={name} setName={setName} unlimited={unlimited} setUnlimited={setUnlimited} sizeText={sizeText} setSizeText={setSizeText} sizeError={sizeError} nameInvalid={field === 'name'} sizeInvalid={field === 'storageLimit'} />
  </FormDialog>
}
