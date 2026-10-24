import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { Loader2, RefreshCw } from 'lucide-react'
import { RelativeTime } from '@/components/data/relative-time'
import { StatusPill } from '@/components/data/status-pill'
import { Button } from '@/components/ui/button'
import { Field } from '@/components/ui/field'
import { inputClass } from '@/components/ui/form-styles'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { Section } from '@/components/ui/section'
import { TechnicalDetails, type TechnicalItem } from '@/components/ui/technical-details'
import type { Agent } from '@/features/agents/types'
import { useRescanAgent, useSaveSettings } from '@/features/discovery/hooks'
import { plainReasonText, reasonText, sourceLabels, supportsDiscovery } from '@/features/discovery/status'
import type { DiscoveryAgentStatus } from '@/features/discovery/types'
import { useTenant } from '@/features/tenancy/use-tenant'
import { ApiError } from '@/lib/api/client'
import { failureMessage } from '@/lib/api/failure-messages'
import { formatDateTime } from '@/lib/format'

/** Interval, Docker and on/off for this computer's automatic scans; an administrator-only technical setting. */
function SettingsForm({ status }: { status: DiscoveryAgentStatus }) {
  useLanguage()
  const save = useSaveSettings(status.agentId)
  const [enabled, setEnabled] = useState(status.enabled)
  const [interval, setInterval] = useState(String(status.scanIntervalMinutes))
  const [includeDocker, setIncludeDocker] = useState(status.includeDocker)
  const minutes = Number(interval)
  const invalid = !Number.isInteger(minutes) || minutes < 15 || minutes > 10080
  const fieldError = save.error instanceof ApiError ? Object.values(save.error.fields).map(code => failureMessage(code))[0] : undefined
  return <form className="space-y-3" aria-label={tx("Tarama ayarları")} onSubmit={event => { event.preventDefault(); if (!invalid) save.mutate({ enabled, scanIntervalMinutes: minutes, includeDocker }) }}>
    <label className="flex min-h-11 items-center gap-3 text-[0.9375rem] text-text"><input type="checkbox" className="size-5 accent-primary" checked={enabled} onChange={event => setEnabled(event.target.checked)} />{tx("Otomatik tarama açık")}</label>
    <Field id={`interval-${status.agentId}`} label={tx("Tarama aralığı (dakika)")} hint={tx("15 ile 10080 arasında.")} error={invalid ? tx('Aralık 15 ile 10080 dakika arasında bir tam sayı olmalı.') : fieldError}>
      {control => <input {...control} type="number" className={inputClass} value={interval} onChange={event => setInterval(event.target.value)} />}
    </Field>
    <label className="flex min-h-11 items-center gap-3 text-[0.9375rem] text-text"><input type="checkbox" className="size-5 accent-primary" checked={includeDocker} onChange={event => setIncludeDocker(event.target.checked)} />{tx("Docker container'larını da tara")}</label>
    {save.error && !fieldError && <ErrorNotice error={save.error} />}
    {save.isSuccess && <Notice tone="success">{tx("Tarama ayarları kaydedildi.")}</Notice>}
    <Button type="submit" disabled={invalid || save.isPending}>{save.isPending ? tx('Kaydediliyor…') : tx('Kaydet')}</Button>
  </form>
}

/** The one plain answer to "is the list of databases current?": a status pill and a sentence. */
function ScanSummary({ supported, status }: { supported: boolean; status: DiscoveryAgentStatus | undefined }) {
  useLanguage()
  if (!supported) return <div className="space-y-1">
    <StatusPill level="attention">{tx("Güncelleme gerekli")}</StatusPill>
    <p className="text-sm text-text-muted">{tx("Bu bilgisayardaki program veritabanı bulmayı desteklemiyor. Programı güncelledikten sonra tarayabilirsiniz.")}</p>
  </div>
  if (!status) return <div className="space-y-1"><StatusPill level="neutral">{tx("Henüz taranmadı")}</StatusPill><p className="text-sm text-text-muted">{tx("Bu bilgisayar henüz taranmadı.")}</p></div>
  if (status.scanState !== 'Idle') return <span className="flex items-center gap-2" role="status" aria-label={tx("Tarama durumu")}><Loader2 aria-hidden="true" className="size-4 animate-spin text-info" /><StatusPill domain="scanState" status={status.scanState} /></span>
  const when = <p className="text-sm text-text-muted">{tx("Son tarama:")}{' '}<RelativeTime value={status.lastScanAt} /></p>
  if (status.lastOutcome === 'Failed') return <div className="space-y-2" role="status" aria-label={tx("Tarama durumu")}><StatusPill level="urgent">{tx("Tarama başarısız")}</StatusPill>{when}<ErrorNotice what={plainReasonText(status.lastFailureCode) ?? tx('Bilgisayara ulaşılamadı ya da tarama tamamlanamadı. Yeniden tarayın.')} dataImpact={tx("Mevcut yedekleriniz etkilenmedi.")} action={tx("Yedekleme bilgisayarının açık ve bağlı olduğunu kontrol edip yeniden deneyin.")} /></div>
  if (status.lastOutcome === 'Partial') return <div className="space-y-1" role="status" aria-label={tx("Tarama durumu")}><StatusPill level="attention">{tx("Kısmen tarandı")}</StatusPill>{when}<p className="text-sm text-text">{tx("Bazı kaynaklar taranamadı; bulunan liste eksik olabilir.")}</p></div>
  if (status.lastOutcome === 'Completed') return <div className="space-y-1" role="status" aria-label={tx("Tarama durumu")}><StatusPill level="safe">{tx("Liste güncel")}</StatusPill>{when}</div>
  return <div className="space-y-1" role="status" aria-label={tx("Tarama durumu")}><StatusPill level="neutral">{tx("Henüz taranmadı")}</StatusPill></div>
}

export function ComputerScanCard({ agent, status }: { agent: Agent; status: DiscoveryAgentStatus | undefined }) {
  useLanguage()
  const { canOperate, canAdmin } = useTenant()
  const rescan = useRescanAgent()
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
  return <Section title={name} actions={canOperate && <Button aria-label={tx("{0} yeniden tara", { "0": name })} disabled={!supported || busy || rescan.isPending} onClick={() => rescan.mutate(agent.agentId)}>
    {busy || rescan.isPending ? <Loader2 aria-hidden="true" className="animate-spin" /> : <RefreshCw aria-hidden="true" />}{tx("Yeniden tara")}</Button>}>
    <div className="space-y-3">
      <ScanSummary supported={supported} status={status} />
      {supported && status && !status.enabled && <p className="text-sm text-warning">{tx("Otomatik tarama kapalı; liste yalnızca elle tarama yapıldığında güncellenir.")}</p>}
      {rescan.error && <ErrorNotice error={rescan.error} />}
      <TechnicalDetails items={items}>{supported && status && canAdmin && <SettingsForm status={status} />}</TechnicalDetails>
    </div>
  </Section>
}
