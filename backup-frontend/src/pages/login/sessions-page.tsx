import { useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { QueryState } from '@/components/data/query-state'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { PageHeader } from '@/components/ui/page-header'
import { useAuth } from '@/features/auth/use-auth'
import { usePageTitle } from '@/hooks/use-page-title'
import { apiRequest } from '@/lib/api/client'
import { formatDateTime } from '@/lib/format'
import { tx, useLanguage } from '@/lib/i18n'

type Session = { id: string; device: string | null; ipAddress: string | null; lastActivityAt: string; isCurrent: boolean }

export function SessionsPage() {
  useLanguage()
  usePageTitle(tx('Oturumlarım'))
  const { clearAuth } = useAuth()
  const cache = useQueryClient()
  const sessions = useQuery({ queryKey: ['auth', 'sessions'], queryFn: ({ signal }) => apiRequest<Session[]>('auth/sessions', { signal }) })
  const [selected, setSelected] = useState<Session | 'all' | null>(null)
  const [pending, setPending] = useState(false)
  const [error, setError] = useState<unknown>(null)
  async function revoke() {
    if (!selected || pending) return
    setPending(true)
    setError(null)
    try {
      await apiRequest<void>(selected === 'all' ? 'auth/sessions' : `auth/sessions/${encodeURIComponent(selected.id)}`, { method: 'DELETE' })
      if (selected === 'all' || selected.isCurrent) {
        cache.removeQueries({ queryKey: ['auth', 'sessions'] })
        await clearAuth()
      } else {
        await cache.invalidateQueries({ queryKey: ['auth', 'sessions'] })
        setSelected(null)
      }
    } catch (error) { setError(error) } finally { setPending(false) }
  }
  const all = selected === 'all'
  return <div className="space-y-6">
    <PageHeader title={tx('Oturumlarım')} description={tx('Etkin oturumlarınızı görüntüleyin ve sonlandırın.')} actions={<Button variant="destructive" disabled={!sessions.data?.length || pending} onClick={() => { setError(null); setSelected('all') }}>{tx('Tüm oturumları sonlandır')}</Button>} />
    <QueryState isPending={sessions.isPending} error={sessions.error} empty={sessions.data?.length === 0} onRetry={() => { void sessions.refetch() }}>
      <Card padding="none"><ul className="divide-y divide-border">{sessions.data?.map(session => <li key={session.id} className="flex flex-wrap items-center justify-between gap-4 p-5">
        <div className="min-w-0 space-y-1"><p className="break-words font-semibold text-text-heading">{session.device || tx('Bilinmiyor')} {session.isCurrent && <span className="text-sm font-normal text-text-muted">· {tx('Bu oturum')}</span>}</p>
          <p className="break-words text-sm text-text-muted">{tx('IP adresi')}: {session.ipAddress || tx('Bilinmiyor')}</p><p className="text-sm text-text-muted">{tx('Son etkinlik')}: {formatDateTime(session.lastActivityAt)}</p></div>
        <Button variant="outline" size="sm" disabled={pending} aria-label={tx('{0} oturumunu sonlandır', { '0': session.device || tx('Bilinmiyor') })} onClick={() => { setError(null); setSelected(session) }}>{tx('Oturumu sonlandır')}</Button>
      </li>)}</ul></Card>
    </QueryState>
    <ConfirmDialog open={selected !== null} onClose={() => setSelected(null)} title={all ? tx('Tüm oturumlar sonlandırılsın mı?') : tx('Oturum sonlandırılsın mı?')} consequence={{ what: all ? tx('Tüm oturumlarınız sonlandırılır.') : tx('Seçilen oturum sonlandırılır.'), who: all ? tx('Bu cihaz dahil tüm cihazlarda çıkış yapılır.') : selected?.isCurrent ? tx('Bu cihazda çıkış yapılır.') : tx('Seçilen cihazda çıkış yapılır.'), reversible: tx('Yeniden giriş yapabilirsiniz.') }} pending={pending} error={error} onConfirm={() => { void revoke() }} confirmLabel={all ? tx('Tüm oturumları sonlandır') : tx('Oturumu sonlandır')} />
  </div>
}
