import { tx, useLanguage } from '@/lib/i18n'
import { Advanced, FormGrid, Question } from '@/features/backup-jobs/job-form-fields'
import { fieldKit, type FieldErrors } from '@/features/backup-jobs/job-field-kit'

interface Props { name: string; onName: (name: string) => void; enabled: boolean; onEnabled: (enabled: boolean) => void; errors: FieldErrors }

/** Question: what is this job called. */
export function NameStep({ name, onName, enabled, onEnabled, errors }: Props) {
  useLanguage()
  const { text, check } = fieldKit(errors)
  return <>
    <Question title={tx("Bu yedekleme işine ne ad verelim?")} why={tx("İşi listelerde ve bildirimlerde bu adla tanırsınız.")}>
      <FormGrid>{text('name', tx('İş adı'), name, onName)}</FormGrid>
    </Question>
    <Advanced open={Boolean(errors.enabled)}>{check(tx('İş etkin'), enabled, onEnabled)}</Advanced>
  </>
}
