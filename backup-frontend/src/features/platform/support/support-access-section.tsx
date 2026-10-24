import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { Link } from 'react-router-dom'
import { QueryState } from '@/components/data/query-state'
import { Button } from '@/components/ui/button'
import { Section } from '@/components/ui/section'
import { platformPaths } from '../paths'
import { linkClass } from '@/components/ui/form-styles'
import { useSupportSessions } from './hooks'
import { OpenSessionDialog } from './open-session-dialog'
import { SupportSessionTable } from './session-table'

export function SupportAccessSection({ tenantId, tenantName }: { tenantId: string; tenantName: string }) {
  useLanguage()
  const [dialogOpen, setDialogOpen] = useState(false)
  const query = useSupportSessions({ tenantId, page: 1, pageSize: 5 })
  return <Section title={tx("Destek erişimi")} description={tx("Tenant verilerine destek erişimi yalnızca gerekçeli, süreli ve açıkça seçilmiş izinlerle sağlanır. Oturumu açan PlatformAdmin erişimi kullanabilir; tüm işlemler audit kayıtlarına yazılır ve tenant yöneticileri tarafından görülebilir.")}
    actions={<Button onClick={() => setDialogOpen(true)}>{tx("Destek oturumu aç")}</Button>}>
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h3 className="text-sm font-medium text-text-heading">{tx("Son destek oturumları")}</h3>
        <Link className={`${linkClass} text-sm`} to={`${platformPaths.supportSessions}?tenantId=${encodeURIComponent(tenantId)}`}>{tx("Tüm destek oturumları")}</Link>
      </div>
      <QueryState isPending={query.isPending} error={query.error}>
        {query.data && <SupportSessionTable rows={query.data.items} showTenant={false} />}
      </QueryState>
    </div>
    {dialogOpen && <OpenSessionDialog tenantId={tenantId} tenantName={tenantName} onClose={() => setDialogOpen(false)} />}
  </Section>
}
