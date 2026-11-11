import { useState } from 'react'
import { tx, useLanguage } from '@/lib/i18n'
import { Button } from '@/components/ui/button'
import { Field } from '@/components/ui/field'
import { FormDialog } from '@/components/ui/form-dialog'
import { Notice } from '@/components/ui/notice'
import { Section } from '@/components/ui/section'
import { TextInput } from '@/components/ui/text-input'
import { StatusPill } from '@/components/data/status-pill'
import { formatDateTime } from '@/lib/format'
import { useCreateShare, useShares } from '@/features/shares/hooks'
import type { BackupArtifact } from './types'

export function ArtifactSharesSection({
  tenantId,
  artifact,
  canAdmin,
}: {
  tenantId: string
  artifact: BackupArtifact
  canAdmin: boolean
}) {
  useLanguage()
  const { data: rawShares, isPending } = useShares(tenantId, artifact.id)
  const shares = Array.isArray(rawShares) ? rawShares : []
  const createMutation = useCreateShare(tenantId, artifact.id)
  const [dialogOpen, setDialogOpen] = useState(false)
  const [durationHours, setDurationHours] = useState(24)
  const [reason, setReason] = useState('')
  const [localError, setLocalError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

  const hasDownloadableReplica = artifact.replicas.some(
    (r) => r.verificationState === 'Verified' && r.status === 'Available'
  )
  const canCreate = canAdmin && hasDownloadableReplica

  function handleSubmit() {
    setLocalError(null)
    if (durationHours < 1 || durationHours > 72) {
      setLocalError(tx('Süre 1 ile 72 saat arasında olmalıdır.'))
      return
    }

    createMutation.mutate(
      { durationHours, reason: reason.trim() || undefined },
      {
        onSuccess: () => {
          setDialogOpen(false)
          setReason('')
          setDurationHours(24)
          setNotice(tx('Paylaşım başarıyla oluşturuldu.'))
        },
      }
    )
  }
  return (
    <Section
      title={tx('Paylaşımlar')}
      actions={
        canAdmin && (
          <Button
            size="sm"
            disabled={!canCreate}
            onClick={() => {
              setLocalError(null)
              createMutation.reset()
              setDialogOpen(true)
            }}
          >
            {tx('Paylaşım oluştur')}
          </Button>
        )
      }
    >
      <div className="space-y-4">
        {notice && <Notice tone="success">{notice}</Notice>}
        {!hasDownloadableReplica && (
          <Notice tone="info">
            {tx('İndirilebilir doğrulanmış bir kopya bulunmadığından paylaşım oluşturulamaz.')}
          </Notice>
        )}

        {isPending ? (
          <p className="text-sm text-text-muted">{tx('Yükleniyor…')}</p>
        ) : !shares || shares.length === 0 ? (
          <p className="text-sm text-text-muted">
            {tx('Bu yedek dosyası için henüz paylaşım bulunmuyor.')}
          </p>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full text-left text-sm">
              <thead className="border-b border-border text-xs text-text-muted uppercase">
                <tr>
                  <th className="py-2 pr-4">{tx('Oluşturan')}</th>
                  <th className="py-2 pr-4">{tx('Rol')}</th>
                  <th className="py-2 pr-4">{tx('Oluşturulma')}</th>
                  <th className="py-2 pr-4">{tx('Bitiş')}</th>
                  <th className="py-2 pr-4">{tx('Durum')}</th>
                  <th className="py-2">{tx('Gerekçe')}</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-border">
                {shares.map((share) => (
                  <tr key={share.id}>
                    <td className="py-2 pr-4 font-medium text-text">
                      {share.createdByDisplayName || share.createdByUserId}
                    </td>
                    <td className="py-2 pr-4 text-text-muted">{share.createdByRole}</td>
                    <td className="py-2 pr-4 text-text-muted">{formatDateTime(share.createdAtUtc)}</td>
                    <td className="py-2 pr-4 text-text-muted">{formatDateTime(share.expiresAtUtc)}</td>
                    <td className="py-2 pr-4">
                      <StatusPill domain="share" status={share.status} />
                    </td>
                    <td className="py-2 text-text-muted">{share.reason || '—'}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {dialogOpen && (
        <FormDialog
          title={tx('Yeni paylaşım oluştur')}
          submitLabel={tx('Paylaşım oluştur')}
          pendingLabel={tx('Oluşturuluyor…')}
          pending={createMutation.isPending}
          error={createMutation.error}
          onSubmit={handleSubmit}
          onClose={() => setDialogOpen(false)}
        >
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
        </FormDialog>
      )}
    </Section>
  )
}
