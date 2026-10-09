import { useId, useState } from 'react'
import { Link } from 'react-router-dom'
import { QueryState } from '@/components/data/query-state'
import { Button } from '@/components/ui/button'
import { platformPaths } from '../paths'
import { linkClass } from '../styles'
import { useSupportSessions } from './hooks'
import { OpenSessionDialog } from './open-session-dialog'
import { SupportSessionTable } from './session-table'

export function SupportAccessSection({ tenantId, tenantName }: { tenantId: string; tenantName: string }) {
  const headingId = useId()
  const [dialogOpen, setDialogOpen] = useState(false)
  const query = useSupportSessions({ tenantId, page: 1, pageSize: 5 })
  return <section className="space-y-4 rounded-lg border border-border bg-surface p-4" aria-labelledby={headingId}>
    <div className="flex flex-wrap items-center justify-between gap-3">
      <h2 id={headingId} className="font-medium text-text-heading">Destek erişimi</h2>
      <Button onClick={() => setDialogOpen(true)}>Destek oturumu aç</Button>
    </div>
    <p className="text-sm text-text-muted">Tenant verilerine destek erişimi yalnızca gerekçeli, süreli ve açıkça seçilmiş izinlerle sağlanır. Oturumu açan PlatformAdmin erişimi kullanabilir; tüm işlemler audit kayıtlarına yazılır ve tenant yöneticileri tarafından görülebilir.</p>
    <div className="flex flex-wrap items-center justify-between gap-2">
      <h3 className="text-sm font-medium text-text-heading">Son destek oturumları</h3>
      <Link className={`${linkClass} text-sm`} to={`${platformPaths.supportSessions}?tenantId=${encodeURIComponent(tenantId)}`}>Tüm destek oturumları</Link>
    </div>
    <QueryState isPending={query.isPending} error={query.error}>
      {query.data && <SupportSessionTable rows={query.data.items} showTenant={false} />}
    </QueryState>
    {dialogOpen && <OpenSessionDialog tenantId={tenantId} tenantName={tenantName} onClose={() => setDialogOpen(false)} />}
  </section>
}
