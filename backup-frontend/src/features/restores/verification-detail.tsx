import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { Fact, Facts, Section } from '@/components/ui/section'
import { TypedConfirmDialog } from '@/components/ui/confirm-dialog'
import { useTenant } from '@/features/tenancy/use-tenant'
import { useRemoveRollbackCopy, useRetryRestoreCleanup } from './hooks'
import { isActiveRestore, verificationCheckLabel, verificationCleanupText } from './presentation'
import type { RestoreView } from './types'
import { formatDateTime, formatDuration } from '@/lib/format'
import { StatusPill } from '@/components/data/status-pill'
import { describeFailure } from '@/lib/api/failure-messages'

/** Detail of a disposable-database restore test: outcome, checks and whether the temporary database is gone. */
export function VerificationDetail({ restore }: { restore: RestoreView }) {
  useLanguage()
  const { canOperate } = useTenant()
  const retry = useRetryRestoreCleanup(restore.id)
  const verification = restore.result?.verification
  const cleanup = restore.cleanup
  return <>
    <Section title={tx("Restore testi sonucu")} description={tx("Test geçici bir Database'e geri yükler; kullanıcının Database'i değiştirilmez.")}>
      <div className="space-y-4">
        <Facts columns={3}>
          <Fact label={tx("Sonuç")}>{restore.verificationOutcome ? <StatusPill domain="verification" status={restore.verificationOutcome} /> : <StatusPill status={restore.status} />}</Fact>
          <Fact label={tx("Süre")}>{restore.completedAt ? formatDuration(restore.startedAt, restore.completedAt) : '—'}</Fact>
          {verification && <Fact label={tx("Tablolar")}>{verification.readableTableCount ?? '—'}{' '}{tx("okunabilir /")}{' '}{verification.userTableCount ?? '—'}{' '}{tx("kullanıcı tablosu")}</Fact>}
        </Facts>
        {restore.verificationOutcome === 'Failed' && (restore.reason || restore.failureCode) && <ErrorNotice {...describeFailure(restore.failureCode, 'restore')} {...(restore.reason ? { what: restore.reason } : {})} />}
        {verification && verification.checks.length > 0 && <ul aria-label={tx("Doğrulama kontrolleri")} className="divide-y divide-border text-sm">{verification.checks.map((check) => <li key={check.checkId} className="flex flex-wrap justify-between gap-2 py-3">
          <span className="min-w-0 break-words">{verificationCheckLabel(check.checkId)} · {check.message}</span><span className="font-medium">{check.status === 'Passed' ? tx('Geçti') : tx('Başarısız')}</span></li>)}</ul>}
      </div>
    </Section>
    {cleanup && <Section title={tx("Geçici Database")}>
      <div className="space-y-3">
        <p className="text-sm">{verificationCleanupText(cleanup)}{cleanup.databaseName && <> · <code className="font-mono">{cleanup.databaseName}</code></>}</p>
        {cleanup.reason && cleanup.state !== 'Completed' && <p className="text-sm text-text-muted">{cleanup.reason}</p>}
        {cleanup.needsAttention && <Notice tone="warning">{tx("Otomatik denemeler tükendi. Geçici Database'in elle kaldırılması gerekebilir.")}</Notice>}
        {cleanup.state === 'Pending' && !isActiveRestore(restore.status) && cleanup.nextAttemptAt && <p className="text-xs text-text-muted">{tx("Sonraki deneme:")}{' '}{formatDateTime(cleanup.nextAttemptAt)}</p>}
        {canOperate && cleanup.state === 'Pending' && cleanup.canRequest && <>
          <Button variant="outline" disabled={retry.isPending} onClick={() => retry.mutate('')}>{retry.isPending ? tx('İşleniyor…') : tx('Temizliği yeniden dene')}</Button>
          {retry.error && <ErrorNotice error={retry.error} scope="restore" />}
        </>}
      </div>
    </Section>}
  </>
}

/** An ordinary replace restore keeps the previous database; only a tenant administrator may ask for its removal. */
export function RollbackCopyPanel({ restore }: { restore: RestoreView }) {
  useLanguage()
  const { canAdmin } = useTenant()
  const remove = useRemoveRollbackCopy(restore.id)
  const [open, setOpen] = useState(false)
  const cleanup = restore.cleanup
  if (cleanup?.kind !== 'RollbackCopy' || cleanup.state !== 'Retained') return null
  const name = cleanup.databaseName ?? ''
  async function confirm() {
    if (remove.isPending) return
    try { await remove.mutateAsync(name); setOpen(false) } catch { /* shown in dialog */ }
  }
  return <Section title={tx("Rollback kopyası")} actions={canAdmin && cleanup.canRequest && <Button variant="destructive" onClick={() => { remove.reset(); setOpen(true) }}>{tx("Rollback kopyasını kaldır")}</Button>}>
    <p className="text-sm">{tx("Rollback kopyası saklanıyor:")}{' '}<code className="font-mono">{name}</code>{tx(". Otomatik olarak asla kaldırılmaz.")}</p>
    {cleanup.retainedUntil && <p className="mt-1 text-sm text-text-muted">{tx("Koruma süresi sonu:")}{' '}{formatDateTime(cleanup.retainedUntil)}</p>}
    <TypedConfirmDialog open={open} onClose={() => setOpen(false)} title={tx("Rollback kopyası kaldırılsın mı?")} confirmLabel={tx("Kalıcı olarak kaldır")} name={name}
      pending={remove.isPending} error={remove.error} errorScope="restore" onConfirm={() => void confirm()}
      consequence={{
        what: tx("Önceki veritabanı kalıcı olarak silinir."),
        who: tx("Geri yükleme öncesindeki veriye dönüş yolu kapanır."),
        reversible: tx("Hayır. Silinen veritabanı geri getirilemez."),
      }} />
  </Section>
}
