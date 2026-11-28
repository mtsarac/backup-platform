import { useEffect, useRef, useState } from 'react'
import { tx, useLanguage } from '@/lib/i18n'
import { Button } from '@/components/ui/button'
import { Field } from '@/components/ui/field'
import { FormDialog } from '@/components/ui/form-dialog'
import { Notice } from '@/components/ui/notice'
import { Section } from '@/components/ui/section'
import { TextInput } from '@/components/ui/text-input'
import { DataList, type Column } from '@/components/data/data-list'
import { StatusPill } from '@/components/data/status-pill'
import { formatDateTime } from '@/lib/format'
import { useAuth } from '@/features/auth/use-auth'
import { accountRoleLabel } from '@/features/auth/role-label'
import { useCreateShare, useShares } from '@/features/shares/hooks'
import { canExtendShare, canManageShare, SHARE_DURATION_HOURS, type BackupShareView } from '@/features/shares/types'
import { ShareDurationSelect } from '@/features/shares/share-duration-select'
import { ShareActionDialogs } from '@/features/shares/share-dialogs'
import { ShareDownload } from '@/features/shares/share-download'
import type { BackupArtifact } from './types'

export function ArtifactSharesSection({
  tenantId,
  artifact,
}: {
  tenantId: string
  artifact: BackupArtifact
}) {
  useLanguage()
  const { data: rawShares, isPending } = useShares(tenantId, artifact.id)
  const shares = Array.isArray(rawShares) ? rawShares : []
  const createMutation = useCreateShare(tenantId, artifact.id)
  const [dialogOpen, setDialogOpen] = useState(false)
  const [durationHours, setDurationHours] = useState(24)
  const [reason, setReason] = useState('')
  const [notice, setNotice] = useState<string | null>(null)
  const { user } = useAuth()
  const [extendTarget, setExtendTarget] = useState<BackupShareView | null>(null)
  const [revokeTarget, setRevokeTarget] = useState<BackupShareView | null>(null)

  const columns: Column<BackupShareView>[] = [
    { key: 'creator', header: tx('Oluşturan'), card: 'title', render: (share) => <><span className="font-medium text-text">{share.createdByDisplayName || share.createdByUserId}</span><span className="block text-sm text-text-muted"><span>{accountRoleLabel(share.createdByRole)}</span> · <span>{formatDateTime(share.createdAtUtc)}</span></span></> },
    { key: 'expiresAt', header: tx('Bitiş'), render: (share) => formatDateTime(share.expiresAtUtc) },
    { key: 'status', header: tx('Durum'), card: 'status', render: (share) => <StatusPill domain="share" status={share.status} /> },
    { key: 'reason', header: tx('Gerekçe'), wrap: true, render: (share) => share.reason || '-' },
    { key: 'actions', header: tx('İşlemler'), card: 'actions', render: (share) => canManageShare(share, user?.role) ? (
      <div className="flex flex-col items-stretch gap-2 max-lg:flex-row max-lg:flex-wrap max-lg:justify-end">
        {user?.role === 'PlatformAdmin' && share.status === 'Active' && <ShareDownload share={share} />}
        {canExtendShare(share, user?.role) && <Button size="sm" variant="outline" onClick={() => setExtendTarget(share)}>{tx('Süreyi uzat')}</Button>}
        <Button size="sm" variant="outline" onClick={() => setRevokeTarget(share)}>{tx('İptal et')}</Button>
      </div>
    ) : '-' },
  ]

  const hasDownloadableReplica = artifact.replicas.some(
    (r) => r.verificationState === 'Verified' && r.status === 'Available'
  )
  const canCreate = hasDownloadableReplica

  const sectionRef = useRef<HTMLDivElement>(null)
  // Opened from the action group above: bring the section into view and start reading it from its heading.
  useEffect(() => { sectionRef.current?.focus() }, [])

  function handleSubmit() {
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
    <div ref={sectionRef} tabIndex={-1} className="focus:outline-none">
    <Section
      title={tx('Paylaşımlar')}
      actions={
        <Button
          size="sm"
          disabled={!canCreate}
          onClick={() => {
            createMutation.reset()
            setDialogOpen(true)
          }}
        >
          {tx('Paylaşım oluştur')}
        </Button>
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
          <DataList
            rows={shares}
            columns={columns}
            keyExtractor={(share) => share.id}
            emptyMessage={tx('Bu yedek dosyası için henüz paylaşım bulunmuyor.')}
          />
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
          <Notice tone="warning" title={tx('Platform yöneticisi bu yedek dosyasını indirebilir')}>
            {tx('Seçtiğiniz süre boyunca yedeğin tamamına erişir; süre dolunca erişim kapanır. İstediğiniz zaman “İptal et” ile paylaşımı geri alabilirsiniz.')}
          </Notice>
          <ShareDurationSelect label={tx('Süre')} hint={tx('Varsayılan: 24 saat.')} hours={SHARE_DURATION_HOURS} value={durationHours} onChange={setDurationHours} />
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
      <ShareActionDialogs
        tenantId={tenantId}
        extendTarget={extendTarget}
        revokeTarget={revokeTarget}
        onClose={() => {
          setExtendTarget(null)
          setRevokeTarget(null)
        }}
        onSuccess={(msg) => setNotice(msg)}
      />
    </Section>
    </div>

  )
}
