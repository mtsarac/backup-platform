import { useState } from 'react'
import { Field } from '@/components/ui/field'
import { FormDialog } from '@/components/ui/form-dialog'
import { TextInput } from '@/components/ui/text-input'
import { BackupPicker } from '@/features/artifacts/backup-picker'
import { TenantPicker } from '@/features/platform/tenant-picker'
import { useCreatePlatformShare } from '@/features/shares/hooks'
import { ShareDurationSelect } from '@/features/shares/share-duration-select'
import { SHARE_DURATION_HOURS } from '@/features/shares/types'
import { tx, useLanguage } from '@/lib/i18n'

export function CreatePlatformShareDialog({
  initialTenantId = '',
  onClose,
}: {
  initialTenantId?: string
  onClose: () => void
}) {
  useLanguage()
  const [tenantId, setTenantId] = useState(initialTenantId)
  const [artifactId, setArtifactId] = useState('')
  const [durationHours, setDurationHours] = useState(24)
  const [reason, setReason] = useState('')
  const [localError, setLocalError] = useState<string | null>(null)

  const mutation = useCreatePlatformShare()

  function handleSubmit() {
    setLocalError(null)
    if (!tenantId) {
      setLocalError(tx('Lütfen bir tenant seçin.'))
      return
    }
    if (!artifactId) {
      setLocalError(tx('Lütfen bir yedek dosyası seçin.'))
      return
    }

    mutation.mutate(
      {
        tenantId,
        artifactId,
        durationHours,
        reason: reason.trim() || undefined,
      },
      {
        onSuccess: () => {
          onClose()
        },
      }
    )
  }

  return (
    <FormDialog
      title={tx('Yeni paylaşım oluştur')}
      submitLabel={tx('Paylaşım oluştur')}
      pendingLabel={tx('Oluşturuluyor…')}
      pending={mutation.isPending}
      error={mutation.error}
      onSubmit={handleSubmit}
      onClose={onClose}
    >
      <div className="space-y-4">
        <div className="space-y-1">
          <TenantPicker
            value={tenantId}
            onChange={(id) => {
              setTenantId(id)
              setArtifactId('')
            }}
          />
        </div>

        {tenantId ? (
          <div className="space-y-1">
            <BackupPicker
              tenantId={tenantId}
              value={artifactId}
              onChange={setArtifactId}
            />
          </div>
        ) : (
          <p className="text-xs text-text-muted">
            {tx('Yedek dosyası seçmek için önce bir tenant belirleyin.')}
          </p>
        )}

        <ShareDurationSelect label={tx('Süre')} hint={tx('Varsayılan: 24 saat.')} hours={SHARE_DURATION_HOURS} value={durationHours} onChange={setDurationHours} />

        {localError && <p role="alert" className="text-sm text-error">{localError}</p>}

        <Field label={tx('Gerekçe (isteğe bağlı)')}>
          {(fieldProps) => (
            <TextInput
              {...fieldProps}
              value={reason}
              onChange={(e) => setReason(e.target.value)}
              placeholder={tx('Paylaşım gerekçesini yazın')}
            />
          )}
        </Field>
      </div>
    </FormDialog>
  )
}
