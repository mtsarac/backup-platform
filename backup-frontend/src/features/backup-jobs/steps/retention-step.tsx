import { tx, useLanguage } from '@/lib/i18n'
import { Advanced, FormGrid, PresetChoice, Question } from '@/features/backup-jobs/job-form-fields'
import { fieldKit, type FieldErrors } from '@/features/backup-jobs/job-field-kit'
import { keepForLabel, retentionPresets } from '@/features/backup-jobs/job-wizard-model'
import type { RetentionPolicy } from '@/features/backup-jobs/types'

interface Props { errors: FieldErrors; retention: RetentionPolicy; onRetention: (retention: RetentionPolicy) => void; canAdmin: boolean; existing: boolean }

/** Question: how long backups are kept. Exact seconds and the delete grace period stay under the technical details. */
export function RetentionStep({ errors, retention, onRetention, canAdmin, existing }: Props) {
  useLanguage()
  const { number } = fieldKit(errors)
  const custom = retention.enabled && !retentionPresets.some(([seconds]) => seconds === retention.keepForSeconds)
  return <>
    <Question title={tx("Yedekler ne kadar süre saklansın?")} why={tx("Süresi dolan yedekler silinir; böylece depolama alanı gereksiz yere dolmaz.")}>
      <FormGrid>
        {!canAdmin && <p className="text-sm text-text-muted md:col-span-2">{tx("Saklama politikasını yalnızca Owner / Admin değiştirebilir.")}{' '}{existing ? tx('Mevcut politika korunur.') : tx('Sunucunun varsayılan politikası uygulanır.')}</p>}
        <PresetChoice label={tx("Saklama süresi")} disabled={!canAdmin} options={[
          ...retentionPresets.map(([seconds]) => ({ text: keepForLabel(seconds), pressed: retention.enabled && retention.keepForSeconds === seconds, onSelect: () => onRetention({ ...retention, enabled: true, keepForSeconds: seconds }) })),
          { text: tx('Süresiz'), pressed: !retention.enabled, onSelect: () => onRetention({ ...retention, enabled: false }) },
        ]} />
        {custom && <p className="text-sm text-text-muted md:col-span-2">{tx("Özel bir saklama süresi kullanılıyor ({0}); ayrıntıları aşağıdaki Teknik ayrıntılar bölümünde görebilirsiniz.", { '0': keepForLabel(retention.keepForSeconds) })}</p>}
        <p className="text-sm text-text-muted md:col-span-2">{tx("Politika değişikliği mevcut yedeklerin son kullanma tarihini değiştirmez.")}</p>
      </FormGrid>
    </Question>
    <Advanced open={Object.keys(errors).some(field => field.startsWith('retention.'))}>
      {number('retention.keepForSeconds', tx('KeepFor — saklama süresi (saniye)'), retention.keepForSeconds, value => onRetention({ ...retention, keepForSeconds: value ?? 0 }), !canAdmin)}
      {number('retention.deleteGracePeriodSeconds', tx('DeleteGracePeriod — silme bekleme süresi (saniye)'), retention.deleteGracePeriodSeconds, value => onRetention({ ...retention, deleteGracePeriodSeconds: value ?? 0 }), !canAdmin)}
    </Advanced>
  </>
}
