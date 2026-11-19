import { tx, useLanguage } from '@/lib/i18n'
import { useEffect } from 'react'
import { Link } from 'react-router-dom'
import { Loader2, RefreshCw } from 'lucide-react'
import { RelativeTime } from '@/components/data/relative-time'
import { StatusPill } from '@/components/data/status-pill'
import { Button } from '@/components/ui/button'
import { linkClass } from '@/components/ui/form-styles'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { Section } from '@/components/ui/section'
import { TechnicalDetails, type TechnicalItem } from '@/components/ui/technical-details'
import type { Agent } from '@/features/agents/types'
import { useRescanAgent } from '@/features/discovery/hooks'
import { isRescanThrottled, plainReasonText, reasonText, sourceLabels, supportsDiscovery } from '@/features/discovery/status'
import type { DiscoveryAgentStatus } from '@/features/discovery/types'
import { useTenant } from '@/features/tenancy/use-tenant'
import { failureMessage } from '@/lib/api/failure-messages'
import { formatDateTime } from '@/lib/format'

/** The one plain answer to "is the list of databases current?": a status pill and a sentence. */
function ScanSummary({ supported, status }: { supported: boolean; status: DiscoveryAgentStatus | undefined }) {
  useLanguage()
  if (!supported) return <div className="space-y-1">
    <StatusPill level="attention">{tx("Güncelleme gerekli")}</StatusPill>
    <p className="text-sm text-text-muted">{tx("Bu bilgisayardaki program veritabanı bulmayı desteklemiyor. Programı güncelledikten sonra tarayabilirsiniz.")}</p>
  </div>
  if (!status) return <div className="space-y-1"><StatusPill level="neutral">{tx("Henüz taranmadı")}</StatusPill><p className="text-sm text-text-muted">{tx("Bu bilgisayar henüz taranmadı.")}</p></div>
  if (status.scanState !== 'Idle') return <span className="flex items-center gap-2" role="status" aria-label={tx("Tarama durumu")}><Loader2 aria-hidden="true" className="size-4 animate-spin text-primary" /><StatusPill domain="scanState" status={status.scanState} /></span>
  const when = <p className="text-sm text-text-muted">{tx("Son tarama:")}{' '}<RelativeTime value={status.lastScanAt} /></p>
  if (status.lastOutcome === 'Failed') return <div className="space-y-2" role="status" aria-label={tx("Tarama durumu")}><StatusPill level="urgent">{tx("Tarama başarısız")}</StatusPill>{when}<ErrorNotice what={plainReasonText(status.lastFailureCode) ?? tx('Bilgisayara ulaşılamadı ya da tarama tamamlanamadı. Yeniden tarayın.')} action={tx("Yedekleme bilgisayarının açık ve bağlı olduğunu kontrol edip yeniden deneyin.")} /></div>
  if (status.lastOutcome === 'Partial') return <div className="space-y-1" role="status" aria-label={tx("Tarama durumu")}><StatusPill level="attention">{tx("Kısmen tarandı")}</StatusPill>{when}<p className="text-sm text-text">{tx("Bazı kaynaklar taranamadı; bulunan liste eksik olabilir.")}</p></div>
  if (status.lastOutcome === 'Completed') return <div className="space-y-1" role="status" aria-label={tx("Tarama durumu")}><StatusPill level="safe">{tx("Liste güncel")}</StatusPill>{when}</div>
  return <div className="space-y-1" role="status" aria-label={tx("Tarama durumu")}><StatusPill level="neutral">{tx("Henüz taranmadı")}</StatusPill></div>
}

export function ComputerScanCard({ agent, status }: { agent: Agent; status: DiscoveryAgentStatus | undefined }) {
  useLanguage()
  const { tenantId, canOperate } = useTenant()
  const rescan = useRescanAgent()
  const throttled = isRescanThrottled(rescan.error)
  const { reset } = rescan
  useEffect(() => {
    if (!throttled) return
    const timer = setTimeout(reset, 6000)
    return () => clearTimeout(timer)
  }, [throttled, reset])
  const name = agent.hostName || tx('Adsız bilgisayar')
  const supported = supportsDiscovery(agent.capabilities)
  const busy = status ? status.scanState !== 'Idle' : false
  const items: TechnicalItem[] = status ? [
    { label: tx("Bilgisayar kimliği"), value: agent.agentId },
    { label: tx("Tarama durumu (ham)"), value: status.scanState },
    ...(status.scanReason ? [{ label: tx("Tarama nedeni"), value: status.scanReason }] : []),
    ...(status.lastOutcome ? [{ label: tx("Son tarama sonucu"), value: status.lastOutcome }] : []),
    ...(status.lastFailureCode ? [{ label: tx("Son hata kodu"), value: status.lastFailureCode }] : []),
    { label: tx("Son tarama"), value: formatDateTime(status.lastScanAt, tx('Henüz bildirilmedi')) },
    { label: tx("Son başarılı tarama"), value: formatDateTime(status.lastSuccessfulScanAt, tx('Henüz bildirilmedi')) },
    { label: tx("Sonraki tarama"), value: formatDateTime(status.nextScanDueAt, tx('Planlanmamış')) },
    { label: tx("Tarama aralığı (dakika)"), value: String(status.scanIntervalMinutes) },
    { label: tx("Bulunan veritabanı sunucusu"), value: String(status.instanceCount) },
    ...status.sources.map(source => ({ label: tx("Kaynak: {0}", { "0": sourceLabels[source.source] ?? source.source }), value: [source.status, reasonText(source.reasonCode), source.source === 'Docker' && source.status === 'Unavailable' ? tx("container'lar listelenmedi; Docker yok demek değildir") : null].filter(Boolean).join(' · ') })),
  ] : [{ label: tx("Bilgisayar kimliği"), value: agent.agentId }]
  return <Section title={<Link className={linkClass} to={`/tenants/${tenantId}/agents/${encodeURIComponent(agent.agentId)}`}>{name}</Link>} actions={canOperate && <Button variant="outline" size="sm" aria-label={tx("{0} yeniden tara", { "0": name })} disabled={!supported || busy || rescan.isPending} onClick={() => rescan.mutate(agent.agentId)}>
    {busy || rescan.isPending ? <Loader2 aria-hidden="true" className="animate-spin" /> : <RefreshCw aria-hidden="true" />}{tx("Yeniden tara")}</Button>}>
    <div className="space-y-3">
      <ScanSummary supported={supported} status={status} />
      {supported && agent.connectivity === 'offline' && <p className="text-sm text-warning">{tx("Bu bilgisayar şu anda çevrimdışı; tarama bilgisayar yeniden bağlandığında çalışır.")}</p>}
      {throttled ? <Notice>{failureMessage('rescan_too_soon')}</Notice> : rescan.error && <ErrorNotice error={rescan.error} />}
      <TechnicalDetails items={items} />
    </div>
  </Section>
}
