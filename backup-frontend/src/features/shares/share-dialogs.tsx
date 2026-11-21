import { useState } from 'react'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { FormDialog } from '@/components/ui/form-dialog'
import { useExtendShare, useRevokeShare } from './hooks'
import { ShareDurationSelect } from './share-duration-select'
import { extensionHours, type BackupShareView } from './types'
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

  const [additionalHours, setAdditionalHours] = useState(1)

  const options = extendTarget ? extensionHours(extendTarget) : []
  const chosenHours = options.includes(additionalHours) ? additionalHours : options[0]

  function handleExtendSubmit() {
    if (!extendTarget || chosenHours === undefined) return
    extendMutation.mutate(
      { shareId: extendTarget.id, request: { additionalHours: chosenHours } },
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
          <ShareDurationSelect
            label={tx('Ek süre')}
            hint={tx('Paylaşım oluşturulma anından itibaren toplamda en fazla 72 saate kadar uzatılabilir.')}
            hours={options}
            value={chosenHours}
            onChange={setAdditionalHours}
          />
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
            who: tx('Bu paylaşıma erişen Platform yöneticileri etkilenir.'),
            reversible: tx('Hayır, iptal edilen bir paylaşım tekrar etkinleştirilemez.'),
          }}
        />
      )}
    </>
  )
}
