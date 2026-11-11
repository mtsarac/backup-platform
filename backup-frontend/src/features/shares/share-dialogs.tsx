import { useState } from 'react'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { Field } from '@/components/ui/field'
import { FormDialog } from '@/components/ui/form-dialog'
import { TextInput } from '@/components/ui/text-input'
import { useExtendShare, useRevokeShare } from './hooks'
import type { BackupShareView } from './types'
import { tx, useLanguage } from '@/lib/i18n'

export function ShareActionDialogs({
  tenantId,
  extendTarget,
  revokeTarget,
  onClose,
  onSuccess,
}: {
  tenantId?: string
  extendTarget: BackupShareView | null
  revokeTarget: BackupShareView | null
  onClose: () => void
  onSuccess?: (message: string) => void
}) {
  useLanguage()
  const activeTenantId = extendTarget?.tenantId || revokeTarget?.tenantId || tenantId || ''
  const extendMutation = useExtendShare(activeTenantId)
  const revokeMutation = useRevokeShare(activeTenantId)

  const [additionalHours, setAdditionalHours] = useState(24)
  const [localError, setLocalError] = useState<string | null>(null)

  function handleExtendSubmit() {
    if (!extendTarget) return
    setLocalError(null)

    const createdAt = new Date(extendTarget.createdAtUtc).getTime()
    const expiresAt = new Date(extendTarget.expiresAtUtc).getTime()
    const maxExpiry = createdAt + 72 * 60 * 60 * 1000
    const remainingMs = maxExpiry - expiresAt
    const maxAdditionalHours = Math.max(0, Math.floor(remainingMs / (60 * 60 * 1000)))

    if (additionalHours < 1) {
      setLocalError(tx('Süre uzatımı en az 1 saat olmalıdır.'))
      return
    }

    if (additionalHours > maxAdditionalHours) {
      setLocalError(tx('Toplam süre 72 saati aşamaz.'))
      return
    }

    extendMutation.mutate(
      { shareId: extendTarget.id, request: { additionalHours } },
      {
        onSuccess: () => {
          onSuccess?.(tx('Paylaşım başarıyla uzatıldı.'))
          onClose()
        },
      }
    )
  }

  function handleRevokeConfirm() {
    if (!revokeTarget) return
    revokeMutation.mutate(
      { shareId: revokeTarget.id, request: {} },
      {
        onSuccess: () => {
          onSuccess?.(tx('Paylaşım başarıyla iptal edildi.'))
          onClose()
        },
      }
    )
  }

  return (
    <>
      {extendTarget && (
        <FormDialog
          title={tx('Paylaşım süresini uzat')}
          submitLabel={tx('Süreyi uzat')}
          pendingLabel={tx('Uzatılıyor…')}
          pending={extendMutation.isPending}
          error={extendMutation.error}
          onSubmit={handleExtendSubmit}
          onClose={onClose}
        >
          <Field
            label={tx('Ek süre (saat)')}
            hint={tx('Paylaşım oluşturulma anından itibaren toplamda en fazla 72 saate kadar uzatılabilir.')}
            error={localError}
          >
            {(fieldProps) => (
              <TextInput
                {...fieldProps}
                type="number"
                min={1}
                max={72}
                value={additionalHours}
                onChange={(e) => setAdditionalHours(Number(e.target.value))}
                required
              />
            )}
          </Field>
        </FormDialog>
      )}

      {revokeTarget && (
        <ConfirmDialog
          open={Boolean(revokeTarget)}
          onClose={onClose}
          title={tx('Paylaşım iptal edilsin mi?')}
          confirmLabel={tx('İptal et')}
          pending={revokeMutation.isPending}
          error={revokeMutation.error}
          onConfirm={handleRevokeConfirm}
          consequence={{
            what: tx('Paylaşım hemen iptal edilir ve indirme erişimi sonlandırılır.'),
            who: tx('Bu paylaşıma erişen PlatformAdmin kullanıcıları etkilenir.'),
            reversible: tx('Hayır, iptal edilen bir paylaşım tekrar etkinleştirilemez.'),
          }}
        />
      )}
    </>
  )
}
