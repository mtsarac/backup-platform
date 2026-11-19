import { tx, useLanguage } from '@/lib/i18n'
import { useEffect, useRef } from 'react'
import { Link } from 'react-router-dom'
import { buttonVariants } from '@/components/ui/button-variants'
import { Button } from '@/components/ui/button'
import { linkClass } from '@/components/ui/form-styles'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import type { Agent } from '@/features/agents/types'
import { useDiscoveryAgents, useDiscoveryInstances, useRescanAgent } from '@/features/discovery/hooks'
import { canCreateJob, isRescanThrottled, supportsDiscovery } from '@/features/discovery/status'
import { useTenant } from '@/features/tenancy/use-tenant'
import { discoveryInstancePath } from '@/lib/routes'

/** Shown after "Bilgisayar ekle" in the first-backup flow: scans the new computer once and says what to do next. */
export function AddedComputerGuide({ agent, agentId, onDismiss }: { agent: Agent | undefined; agentId: string; onDismiss: () => void }) {
  useLanguage()
  const { tenantId } = useTenant()
  const capable = supportsDiscovery(agent?.capabilities)
  const rescan = useRescanAgent()
  const scanned = useRef(false)
  useEffect(() => {
    if (!capable || scanned.current) return
    scanned.current = true
    rescan.mutate(agentId)
  }, [capable]) // oxlint-disable-line react-hooks/exhaustive-deps -- one automatic scan, not per render
  const { scanning: polling } = useDiscoveryAgents(capable, agentId)
  const instances = useDiscoveryInstances({ agentId }, capable)
  const dismiss = <Button variant="outline" onClick={onDismiss}>{tx('Kapat')}</Button>
  // A throttled rescan means one is already queued: the scan state below tells it.
  if (rescan.error && !isRescanThrottled(rescan.error)) return <ErrorNotice error={rescan.error} onRetry={() => rescan.mutate(agentId)} />

  const scanning = !capable || rescan.isIdle || rescan.isPending || polling || !instances.data
  const found = instances.data?.items.filter(item => item.presence === 'Present') ?? []
  const usable = found.find(item => canCreateJob(item) && item.databaseCounts.backupEligible > 0)
  const needsLogin = found.filter(item => item.access === 'CredentialsRequired')
  const jobsNew = `/tenants/${tenantId}/backup-jobs/new`
  const next = <Link className={buttonVariants()} to={`/tenants/${tenantId}/first-backup`}>{tx('İlk yedeği almaya devam et')}</Link>
  const actions = <span className="flex flex-wrap justify-end gap-2">{dismiss}{next}</span>
  if (agent?.connectivity === 'offline') return <Notice tone="info" action={actions}>{tx('Bu bilgisayar şu anda çevrimdışı; tarama bilgisayar yeniden bağlandığında çalışır.')}</Notice>
  if (scanning) return <Notice tone="info" action={actions}>{tx('Bilgisayar eklendi. Veritabanları taranıyor, birkaç saniye sürer.')}</Notice>
  if (usable) return <Notice tone="success" action={<span className="flex flex-wrap justify-end gap-2">{dismiss}<Link className={buttonVariants({ variant: 'outline' })} to={`${jobsNew}?${new URLSearchParams({ discoveryInstance: usable.id })}`}>{tx('Yedekleme işi oluştur')}</Link>{next}</span>}>{tx('Yedeklemek istediğiniz veritabanını seçin.')}</Notice>
  if (needsLogin.length > 0) return <Notice tone="warning" action={actions}>{tx('Bilgisayarda veritabanı sunucusu bulundu. Yedek alabilmek için sunucuyu açıp giriş bilgilerini girin:')} {needsLogin.map((item, index) => <span key={item.id}>{index > 0 && ', '}<Link className={linkClass} to={discoveryInstancePath(tenantId, item.id)}>{item.displayName}</Link></span>)}</Notice>
  return <Notice tone="info" action={actions}>{tx('Bu bilgisayarda veritabanı bulunamadı; dosya yedeklemek veya elle eklemek için')} <Link className={linkClass} to={jobsNew}>{tx('Elle yapılandır')}</Link></Notice>
}
