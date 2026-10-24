import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { Field } from '@/components/ui/field'
import { FormDialog } from '@/components/ui/form-dialog'
import { selectClass } from '@/components/ui/form-styles'
import { useAssignBandwidthProfile, useBandwidthProfiles } from './hooks'
import type { BandwidthTarget } from './types'

export function AssignProfileDialog({ target, name, currentProfileId, onClose }: {
  target: BandwidthTarget; name: string; currentProfileId?: string | null; onClose: () => void
}) {
  useLanguage()
  const profiles = useBandwidthProfiles()
  const [selectedProfileId, setSelectedProfileId] = useState<string>(currentProfileId ?? '')
  const assign = useAssignBandwidthProfile()

  function submit() {
    assign.mutate({ target, profileId: selectedProfileId ? selectedProfileId : null }, { onSuccess: onClose })
  }

  return (
    <FormDialog
      title={tx("Profil Ata: {0}", { "0": name })}
      description={tx("Bu hedefe zaman pencereli bir hız profili atayın veya atamayı kaldırın.")}
      submitLabel={tx("Kaydet")}
      pendingLabel={tx("Kaydediliyor…")}
      pending={assign.isPending}
      error={assign.error}
      onSubmit={submit}
      onClose={onClose}
    >
      <Field label={tx("Bant Genişliği Profili")}>
        {control => (
          <select {...control} className={selectClass} value={selectedProfileId} onChange={e => setSelectedProfileId(e.target.value)}>
            <option value="">{tx("Profil Yok (Yalnızca Statik Limit / Limitsiz)")}</option>
            {(profiles.data ?? []).map(p => (
              <option key={p.id} value={p.id}>{p.name} ({p.timeZoneId})</option>
            ))}
          </select>
        )}
      </Field>
    </FormDialog>
  )
}
