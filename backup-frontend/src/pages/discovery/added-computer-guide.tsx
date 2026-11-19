import { tx, useLanguage } from '@/lib/i18n'
import { useEffect, useRef } from 'react'
import { Link } from 'react-router-dom'
import { buttonVariants } from '@/components/ui/button-variants'
import { Button } from '@/components/ui/button'
import { linkClass } from '@/components/ui/form-styles'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import type { Agent } from '@/features/agents/types'
import { useDiscoveryAgents, useDiscoveryInstances, useRescanAgent } from '@/features/discovery/hooks'
import { canCreateJob, supportsDiscovery } from '@/features/discovery/status'
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
  const { scanning: polling } = useDiscoveryAgents(capable)
  const instances = useDiscoveryInstances({ agentId }, capable)
  const dismiss = <Button variant="outline" onClick={onDismiss}>{tx('Kapat')}</Button>
  if (rescan.error) return <ErrorNotice error={rescan.error} onRetry={() => rescan.mutate(agentId)} />

  const scanning = !capable || rescan.isIdle || rescan.isPending || polling || !instances.data
  const found = instances.data?.items.filter(item => item.presence === 'Present') ?? []
  const usable = found.find(item => canCreateJob(item) && item.databaseCounts.backupEligible > 0)
  const needsLogin = found.find(item => item.access === 'CredentialsRequired')
  const jobsNew = `/tenants/${tenantId}/backup-jobs/new`
  if (scanning) return <Notice tone="info" action={dismiss}>{tx('Bilgisayar eklendi. Veritabanları taranıyor, birkaç saniye sürer.')}</Notice>
  if (usable) return <Notice tone="success" action={<span className="flex gap-2">{dismiss}<Link className={buttonVariants()} to={`${jobsNew}?${new URLSearchParams({ discoveryInstance: usable.id })}`}>{tx('Yedekleme işi oluştur')}</Link></span>}>{tx('Yedeklemek istediğiniz veritabanını seçin.')}</Notice>
  if (needsLogin) return <Notice tone="warning" action={dismiss}>{tx('Bilgisayarda veritabanı sunucusu bulundu. Yedek alabilmek için sunucuyu açıp giriş bilgilerini girin:')} <Link className={linkClass} to={discoveryInstancePath(tenantId, needsLogin.id)}>{needsLogin.displayName}</Link></Notice>
  return <Notice tone="info" action={dismiss}>{tx('Bu bilgisayarda veritabanı bulunamadı; dosya yedeklemek veya elle eklemek için')} <Link className={linkClass} to={jobsNew}>{tx('Elle yapılandır')}</Link></Notice>
}
