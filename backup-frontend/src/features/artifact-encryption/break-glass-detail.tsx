import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { Field } from '@/components/ui/field'
import { Button } from '@/components/ui/button'
import { Notice } from '@/components/ui/notice'
import { Fact, Facts, Section } from '@/components/ui/section'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { FormDialog } from '@/components/ui/form-dialog'
import { inputClass } from '@/components/ui/form-styles'
import { useSupportCountdown } from '@/features/platform/support/use-support-countdown'
import { BreakGlassDownload } from './break-glass-download'
import { encryptionKeys, useBreakGlassDecision } from './hooks'
import type { BreakGlassRequest } from './types'
import { formatDateTime } from '@/lib/format'
import { StatusPill } from '@/components/data/status-pill'

/** `receivedAt` is when the view was fetched: the countdown follows the server clock, not a possibly skewed browser clock. */
export function BreakGlassDetail({ request, receivedAt }: { request: BreakGlassRequest; receivedAt: number }) {
  useLanguage()
  const client = useQueryClient()
  const { approve, deny } = useBreakGlassDecision(request.id)
  const [approving, setApproving] = useState(false)
  const [denying, setDenying] = useState(false)
  const [denyReason, setDenyReason] = useState('')
  const [denyError, setDenyError] = useState('')
  const open = request.status === 'Pending' || request.status === 'Approved'
  const countdown = useSupportCountdown(request.expiresAt, request.serverNow, receivedAt)
  // The server judges expiry; when the local countdown reaches zero the cached view is stale, so ask again instead of offering dead buttons.
  const expiredLocally = open && countdown.seconds === 0
  const refresh = () => { void client.invalidateQueries({ queryKey: encryptionKeys.breakGlass(request.id) }) }

  function submitDeny() {
    const trimmed = denyReason.trim()
    if (!trimmed) { setDenyError(tx('Ret gerekçesi girin.')); return }
    if (trimmed.length > 500) { setDenyError(tx('Gerekçe en fazla 500 karakter olabilir.')); return }
    deny.mutate(trimmed, { onSuccess: () => setDenying(false) })
  }

  return <div className="space-y-6">
    <Section title={tx("İstek")} description={tx("Tek bir şifreli Backup için süreli, gerekçeli ve çift onaylı çözme isteği.")}>
      <Facts>
        <Fact label={tx("Durum")}><StatusPill domain="breakGlass" status={request.status} /></Fact>
        {open && <Fact label={tx("Kalan süre")}><span className="tabular-nums">{countdown.label}</span></Fact>}
        <Fact label={tx("Tenant")}>{request.tenantName ?? request.tenantId}</Fact>
        <Fact label={tx("Backup")}><code className="break-all font-mono text-sm">{request.artifactId}</code></Fact>
        <Fact label={tx("Gerekçe")}>{request.reason}</Fact>
        <Fact label={tx("İsteyen")}>{request.requestedByName ?? request.requestedBy}{request.isRequester && <span className="text-text-muted">{' '}{tx("(siz)")}</span>}</Fact>
        <Fact label={tx("Açılış")}>{formatDateTime(request.createdAt)}</Fact>
        <Fact label={tx("Süre sonu")}>{formatDateTime(request.expiresAt)}</Fact>
        {request.decidedAt && <Fact label={request.status === 'Denied' ? tx("Reddeden") : tx("Onaylayan")}>{request.decidedByName ?? request.decidedBy} · {formatDateTime(request.decidedAt)}</Fact>}
        {request.decisionReason && <Fact label={tx("Ret gerekçesi")}>{request.decisionReason}</Fact>}
        {request.consumedAt && <Fact label={tx("Kullanıldı")}>{formatDateTime(request.consumedAt)}</Fact>}
      </Facts>
    </Section>

    {request.status === 'Pending' && !expiredLocally && <Section title={tx("Onay")}>
      {request.canApprove ? <div className="space-y-3">
        <p className="text-sm text-text-muted">{tx("Onay, isteği açan yöneticinin bu Backup'ı tenant anahtarıyla çözerek tek seferlik indirmesine izin verir. Gerekçeyi ve tenant'ı doğrulamadan onaylamayın.")}</p>
        <div className="flex flex-wrap gap-2">
          <Button onClick={() => { approve.reset(); setApproving(true) }}>{tx("Onayla")}</Button>
          <Button variant="destructive" onClick={() => { deny.reset(); setDenyReason(''); setDenyError(''); setDenying(true) }}>{tx("Reddet")}</Button>
        </div>
      </div> : <Notice tone="info">{request.isRequester ? tx("Bu isteği siz açtınız. Çift kontrol gereği farklı bir PlatformAdmin onaylamalıdır.") : tx("Bu istek onay bekliyor.")}</Notice>}
    </Section>}

    {request.status === 'Approved' && !request.isRequester && <Section title={tx("Çözülmüş indirme")}><Notice tone="info">{tx("İstek onaylandı. Yalnızca isteği açan yönetici, süre dolmadan bir kez indirebilir.")}</Notice></Section>}
    {request.isRequester && (request.status === 'Consumed' || (request.status === 'Approved' && !expiredLocally)) && <Section title={tx("Çözülmüş indirme")}>
      <BreakGlassDownload requestId={request.id} canStart={request.canDownload} />
    </Section>}

    {expiredLocally && <Notice tone="warning" action={<Button size="sm" variant="outline" onClick={refresh}>{tx("Yenile")}</Button>}>{tx("İsteğin süresi doldu. Sunucudaki durum yenilenir; yeni bir istek açmanız gerekir.")}</Notice>}
    {request.status === 'Consumed' && <Notice tone="success">{tx("Bu istek kullanıldı. Tek kullanımlıktır; yeniden indirmek için yeni bir istek açın.")}</Notice>}
    {request.status === 'Denied' && <Notice tone="warning">{tx("Bu istek reddedildi; hiçbir veri çözülmedi.")}</Notice>}
    {request.status === 'Expired' && <Notice tone="warning">{tx("İsteğin süresi doldu; hiçbir veri çözülmedi.")}</Notice>}

    <ConfirmDialog open={approving} onClose={() => setApproving(false)} title={tx("İstek onaylansın mı?")}
      consequence={{
        what: tx("“{0}” çalışma alanındaki yedek, isteği açan yönetici tarafından çözülerek bir kez indirilebilecek.", { "0": request.tenantName ?? request.tenantId }),
        who: tx("Bu çalışma alanının sahipleri bilgilendirilir; işlem denetim kaydına yazılır."),
        reversible: tx("Hayır. Onay verildikten sonra süre dolana kadar tek seferlik indirme açık kalır."),
      }}
      confirmLabel={tx("Onayla")} pending={approve.isPending} error={approve.error}
      onConfirm={() => approve.mutate(undefined, { onSuccess: () => setApproving(false) })} />
    {denying && <FormDialog title={tx("İstek reddedilsin mi?")} description={tx("Ret gerekçesi audit kaydına yazılır.")} submitLabel={tx("Reddet")} pendingLabel={tx("Reddediliyor…")} pending={deny.isPending}
      error={deny.error} onSubmit={submitDeny} onClose={() => setDenying(false)}>
      <Field label={tx("Ret gerekçesi")} error={denyError}>{control => <textarea {...control} className={inputClass} rows={3} maxLength={500} value={denyReason} onChange={event => { setDenyReason(event.target.value); setDenyError('') }} />}</Field>
    </FormDialog>}
  </div>
}
