import { useState } from 'react'
import { Field } from '@/components/ui/field'
import { FormDialog } from '@/components/ui/form-dialog'
import { TextInput } from '@/components/ui/text-input'
import { BackupPicker } from '@/features/artifacts/backup-picker'
import { TenantPicker } from '@/features/platform/tenant-picker'
import { useCreatePlatformShare } from '@/features/shares/hooks'
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
    if (durationHours < 1 || durationHours > 72) {
      setLocalError(tx('Süre 1 ile 72 saat arasında olmalıdır.'))
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

        <Field
          label={tx('Süre (saat)')}
          hint={tx('1 ile 72 saat arasında bir süre seçin (varsayılan: 24 saat).')}
          error={localError}
        >
          {(fieldProps) => (
            <TextInput
              {...fieldProps}
              type="number"
              min={1}
              max={72}
              value={durationHours}
              onChange={(e) => setDurationHours(Number(e.target.value))}
              required
            />
          )}
        </Field>

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
