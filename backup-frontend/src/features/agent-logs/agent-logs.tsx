import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { QueryState } from '@/components/data/query-state'
import { Button } from '@/components/ui/button'
import { controlClass } from '@/components/ui/form-styles'
import { Section } from '@/components/ui/section'
import { apiRequest, tenantPath } from '@/lib/api/client'
import { formatDateTime } from '@/lib/format'
import { tx, useLanguage } from '@/lib/i18n'

type LogEntry = { id: string; timestamp: string; code: string; message: string; level: string; agentId: string; jobId: string | null; runId: string | null }
type Scope = { tenantId: string; agentId?: string; jobId?: string; runId?: string }

export function AgentLogs({ tenantId, agentId, jobId, runId }: Scope) {
  useLanguage()
  const [level, setLevel] = useState('')
  const [text, setText] = useState('')
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')
  const [tail, setTail] = useState(false)
  const parameters = new URLSearchParams()
  if (agentId) parameters.set('agentId', agentId)
  if (jobId) parameters.set('jobId', jobId)
  if (runId) parameters.set('runId', runId)
  if (level) parameters.set('level', level)
  if (text) parameters.set('text', text)
  if (from) parameters.set('from', new Date(from).toISOString())
  if (to) parameters.set('to', new Date(to).toISOString())
  const queryString = parameters.toString()
  const query = useQuery({
    queryKey: ['tenant', tenantId, 'agent-logs', queryString],
    queryFn: ({ signal }) => apiRequest<LogEntry[]>(tenantPath(tenantId, `agent-logs?${queryString}`), { signal }),
    refetchInterval: tail ? 10_000 : false,
  })
  function download() {
    const url = URL.createObjectURL(new Blob([JSON.stringify(query.data, null, 2)], { type: 'application/json' }))
    const link = document.createElement('a')
    link.href = url
    link.download = 'agent-logs.json'
    link.click()
    URL.revokeObjectURL(url)
  }
  return <Section title={tx('Bilgisayar günlükleri')} description={tx('En fazla son 1000 kayıt gösterilir.')} actions={<>
    <label className="flex items-center gap-2"><input type="checkbox" checked={tail} onChange={event => setTail(event.target.checked)} />{tx('Canlı takip')}</label>
    <Button variant="outline" onClick={() => void query.refetch()}>{tx('Yenile')}</Button>
    <Button variant="outline" disabled={!query.data?.length} onClick={download}>{tx('İndir')}</Button>
  </>}>
    <div className="flex flex-wrap gap-3">
      <label>{tx('Seviye')}<select className={controlClass} value={level} onChange={event => setLevel(event.target.value)}><option value="">{tx('Tümü')}</option><option value="Information">{tx('Bilgi')}</option><option value="Warning">{tx('Uyarı')}</option></select></label>
      <label>{tx('Başlangıç')}<input className={controlClass} type="datetime-local" value={from} onChange={event => setFrom(event.target.value)} /></label>
      <label>{tx('Bitiş')}<input className={controlClass} type="datetime-local" value={to} onChange={event => setTo(event.target.value)} /></label>
      <label>{tx('Metin')}<input className={controlClass} value={text} maxLength={200} onChange={event => setText(event.target.value)} /></label>
    </div>
    <div className="mt-4"><QueryState isPending={query.isPending} error={query.error}>
      {query.data?.length ? <div className="overflow-x-auto"><table className="w-full text-left text-sm"><thead><tr><th>{tx('Zaman')}</th><th>{tx('Seviye')}</th><th>{tx('Bileşen')}</th><th>{tx('Mesaj')}</th></tr></thead><tbody>
        {query.data.map(entry => <tr key={entry.id} className="border-t border-border"><td className="py-2 pr-4 whitespace-nowrap">{formatDateTime(entry.timestamp)}</td><td className="pr-4">{entry.level === 'Warning' ? tx('Uyarı') : tx('Bilgi')}</td><td className="pr-4">{entry.code.split('.')[0]}</td><td>{entry.message}</td></tr>)}
      </tbody></table></div> : <p className="text-sm text-text-muted">{tx('Bu filtrelerle günlük kaydı bulunamadı.')}</p>}
    </QueryState></div>
  </Section>
}
