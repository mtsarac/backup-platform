import { useState } from 'react'
import { Loader2, RefreshCw } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Field } from '@/components/ui/field'
import { Notice } from '@/components/ui/notice'
import { Section } from '@/components/ui/section'
import { agentDate } from '@/features/agents/format'
import type { Agent } from '@/features/agents/types'
import { OutcomePill, ScanStatePill, SourceStatusPill } from '@/features/discovery/presentation'
import { useRescanAgent, useSaveSettings } from '@/features/discovery/hooks'
import { reasonText, sourceLabels, supportsDiscovery } from '@/features/discovery/status'
import type { DiscoveryAgentStatus } from '@/features/discovery/types'
import { useTenant } from '@/features/tenancy/use-tenant'
import { inputClass } from '@/features/platform/styles'
import { ApiError } from '@/lib/api/client'
import { errorMessage, failureMessage } from '@/lib/api/failure-messages'

function SettingsForm({ status, onDone }: { status: DiscoveryAgentStatus; onDone: () => void }) {
  const save = useSaveSettings(status.agentId)
  const [enabled, setEnabled] = useState(status.enabled)
  const [interval, setInterval] = useState(String(status.scanIntervalMinutes))
  const [includeDocker, setIncludeDocker] = useState(status.includeDocker)
  const minutes = Number(interval)
  const invalid = !Number.isInteger(minutes) || minutes < 15 || minutes > 10080
  const fieldError = save.error instanceof ApiError ? Object.values(save.error.fields).map(failureMessage)[0] : undefined
  return <form className="mt-3 space-y-3 rounded-lg border border-border bg-surface-2 p-4" aria-label="Keşif ayarları" onSubmit={event => { event.preventDefault(); if (!invalid) save.mutate({ enabled, scanIntervalMinutes: minutes, includeDocker }, { onSuccess: onDone }) }}>
    <label className="flex min-h-11 items-center gap-3 text-[0.9375rem] text-text"><input type="checkbox" className="size-5 accent-primary" checked={enabled} onChange={event => setEnabled(event.target.checked)} />Otomatik keşif etkin</label>
    <Field id={`interval-${status.agentId}`} label="Tarama aralığı (dakika)" hint="15 ile 10080 arasında." error={invalid ? 'Aralık 15 ile 10080 dakika arasında bir tam sayı olmalı.' : fieldError}>
      {control => <input {...control} type="number" className={inputClass} value={interval} onChange={event => setInterval(event.target.value)} />}
    </Field>
    <label className="flex min-h-11 items-center gap-3 text-[0.9375rem] text-text"><input type="checkbox" className="size-5 accent-primary" checked={includeDocker} onChange={event => setIncludeDocker(event.target.checked)} />Docker container'larını tara</label>
    {save.error && !fieldError && <Notice tone="error">{errorMessage(save.error)}</Notice>}
    <div className="flex gap-2"><Button type="submit" disabled={invalid || save.isPending}>{save.isPending ? 'Kaydediliyor…' : 'Kaydet'}</Button><Button type="button" variant="outline" onClick={onDone}>Vazgeç</Button></div>
  </form>
}

export function AgentStatusBar({ agent, status }: { agent: Agent; status: DiscoveryAgentStatus | undefined }) {
  const { canOperate, canAdmin } = useTenant()
  const rescan = useRescanAgent()
  const [editing, setEditing] = useState(false)
  const supported = supportsDiscovery(agent.capabilities)
  const busy = status ? status.scanState !== 'Idle' : false
  return <Section title={agent.hostName || agent.agentId} actions={<>
    {canAdmin && status && <Button variant="outline" size="sm" aria-label={`${agent.hostName || agent.agentId} keşif ayarları`} onClick={() => setEditing(value => !value)}>Ayarlar</Button>}
    {canOperate && <Button size="sm" aria-label={`${agent.hostName || agent.agentId} yeniden tara`} disabled={!supported || busy || rescan.isPending} onClick={() => rescan.mutate(agent.agentId)}>
      {busy || rescan.isPending ? <Loader2 aria-hidden="true" className="animate-spin" /> : <RefreshCw aria-hidden="true" />}Yeniden Tara</Button>}
  </>}>
    {!supported && <p className="text-sm text-text-muted">Agent sürümü keşfi desteklemiyor. Agent'ı güncelledikten sonra yeniden tarama yapılabilir.</p>}
    {supported && !status && <p className="text-sm text-text-muted">Bu Agent henüz tarama yapmadı.</p>}
    {status && <div className="space-y-3">
      <div className="flex flex-wrap items-center gap-x-4 gap-y-2 text-sm text-text">
        <span className="flex items-center gap-2" role="status" aria-label="Tarama durumu">{busy && <Loader2 aria-hidden="true" className="size-4 animate-spin" />}<ScanStatePill value={status.scanState} /></span>
        <span>Son tarama: {agentDate(status.lastScanAt)}</span>
        {status.lastOutcome && <OutcomePill value={status.lastOutcome} />}
        <span>{status.instanceCount} Instance</span>
        {!status.enabled && <span className="text-warning">Otomatik keşif kapalı</span>}
      </div>
      {status.lastOutcome === 'Failed' && <p className="text-sm text-error">{reasonText(status.lastFailureCode) ?? 'Son tarama başarısız oldu.'}</p>}
      {status.sources.length > 0 && <ul className="flex flex-wrap gap-3 text-sm" aria-label="Keşif kaynakları">{status.sources.map(source => <li key={source.source} className="flex flex-wrap items-center gap-2">
        <span className="text-text-muted">{sourceLabels[source.source] ?? source.source}</span><SourceStatusPill value={source.status} />
        {source.source === 'Docker' && source.status === 'Unavailable' && <span className="text-text-muted">Docker CLI bulunamadı veya bu kullanıcı için erişilemiyor; container'lar listelenmedi (Docker yok demek değildir).</span>}
        {!(source.source === 'Docker' && source.status === 'Unavailable') && source.status !== 'Scanned' && reasonText(source.reasonCode) && <span className="text-text-muted">{reasonText(source.reasonCode)}</span>}
      </li>)}</ul>}
      {editing && canAdmin && <SettingsForm status={status} onDone={() => setEditing(false)} />}
    </div>}
    {rescan.error && <Notice className="mt-3" tone="error">{errorMessage(rescan.error)}</Notice>}
  </Section>
}
