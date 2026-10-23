import { currentLocale, tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { DataTable } from '@/components/data/data-table'
import type { Column } from '@/components/data/data-table'
import { QueryState } from '@/components/data/query-state'
import { Pagination } from '@/components/data/pagination'
import { Button } from '@/components/ui/button'
import { EmptyState } from '@/components/ui/empty-state'
import { FilterField, Filters, filterControlClass } from '@/components/ui/filters'
import { Notice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { Section } from '@/components/ui/section'
import { Tabs } from '@/components/ui/tabs'
import { useAgents } from '@/features/agents/hooks'
import type { Agent } from '@/features/agents/types'
import { useBackupJobs } from '@/features/backup-jobs/hooks'
import type { BackupJob } from '@/features/backup-jobs/types'
import { useTenant } from '@/features/tenancy/use-tenant'
import { AssignProfileDialog } from '@/features/upload-bandwidth/assign-profile-dialog'
import { LimitDialog } from '@/features/upload-bandwidth/limit-dialog'
import { ProfileDialog } from '@/features/upload-bandwidth/profile-dialog'
import { useBandwidthProfiles, useDeleteBandwidthProfile, useUploadBandwidth } from '@/features/upload-bandwidth/hooks'
import type { BandwidthProfile, BandwidthTarget } from '@/features/upload-bandwidth/types'
import { formatBandwidth, formatExactRate } from '@/features/upload-bandwidth/units'

type Tab = 'tenant' | 'agents' | 'jobs' | 'profiles'
const PAGE_SIZE = 25

interface Editing { target: BandwidthTarget; name: string; current: number | null }

function Limit({ value }: { value: number | null | undefined }) {
  useLanguage()
  if (value == null) return <span className="text-text-muted">{tx("Limitsiz")}</span>
  return <span>{formatBandwidth(value)} <span className="text-xs text-text-muted">({formatExactRate(value)})</span></span>
}

/** Client-side search and paging: both lists come complete from their own endpoints. */
function SearchableTable<T>({ rows, columns, keyExtractor, text, searchLabel, emptyMessage }: {
  rows: readonly T[]; columns: Column<T>[]; keyExtractor: (row: T) => string; text: (row: T) => string; searchLabel: string; emptyMessage: string
}) {
  useLanguage()
  const [query, setQuery] = useState('')
  const [page, setPage] = useState(1)
  const needle = query.trim().toLocaleLowerCase(currentLocale())
  const matches = needle ? rows.filter(row => text(row).toLocaleLowerCase(currentLocale()).includes(needle)) : rows
  const lastPage = Math.max(1, Math.ceil(matches.length / PAGE_SIZE))
  const current = Math.min(page, lastPage)
  return <div className="space-y-4">
    <Filters label={tx("{0} filtreleri", { "0": searchLabel })}>
      <FilterField label={searchLabel}><input type="search" aria-label={searchLabel} placeholder={searchLabel} className={filterControlClass} value={query} onChange={event => { setQuery(event.target.value); setPage(1) }} /></FilterField>
    </Filters>
    {matches.length === 0 ? <div className="rounded-xl border border-border bg-surface"><EmptyState title={needle ? tx('Aramayla eşleşen kayıt yok.') : emptyMessage} /></div>
      : <DataTable rows={matches.slice((current - 1) * PAGE_SIZE, current * PAGE_SIZE)} columns={columns} keyExtractor={keyExtractor} />}
    {matches.length > PAGE_SIZE && <Pagination label={tx("{0} sayfaları", { "0": searchLabel })} page={current} pageSize={PAGE_SIZE} totalCount={matches.length} onPageChange={setPage} />}
  </div>
}

export function UploadBandwidthPage() {
  useLanguage()
  const { tenant, tenantId, canAdmin } = useTenant()
  const limits = useUploadBandwidth()
  const agents = useAgents(tenantId)
  const jobs = useBackupJobs()
  const profiles = useBandwidthProfiles()
  const deleteProfile = useDeleteBandwidthProfile()
  const [editing, setEditing] = useState<Editing | null>(null)
  const [assigning, setAssigning] = useState<{ target: BandwidthTarget; name: string; currentProfileId?: string | null } | null>(null)
  const [editingProfile, setEditingProfile] = useState<BandwidthProfile | null | 'new'>(null)
  const [tab, setTab] = useState<Tab>('tenant')
  const data = limits.data
  const profileList = profiles.data ?? []
  const profileMap = new Map(profileList.map(p => [p.id, p.name]))
  const agentProfiles = new Map(data?.agents.map(item => [item.agentId, item.profileId]))
  const jobProfiles = new Map(data?.jobs.map(item => [item.jobId, item.profileId]))
  const agentLimits = new Map(data?.agents.map(item => [item.agentId, item.bytesPerSecond]))
  const jobLimits = new Map(data?.jobs.map(item => [item.jobId, item.bytesPerSecond]))
  const edit = (target: BandwidthTarget, name: string, current: number | null | undefined) => canAdmin
    ? <Button variant="outline" size="sm" aria-label={tx("{0} yükleme hız sınırını düzenle", { "0": name })} onClick={() => setEditing({ target, name, current: current ?? null })}>{tx("Düzenle")}</Button>
    : null
  const agentColumns: Column<Agent>[] = [
    { key: 'name', header: tx('Agent'), wrap: true, render: agent => agent.hostName || agent.agentId },
    { key: 'limit', header: tx('Hız sınırı'), wrap: true, render: agent => <Limit value={agentLimits.get(agent.agentId)} /> },
    { key: 'profile', header: tx('Bant Genişliği Profili'), wrap: true, render: agent => agentProfiles.get(agent.agentId) ? profileMap.get(agentProfiles.get(agent.agentId)!) ?? tx('Atandı') : <span className="text-text-muted">—</span> },
    ...(canAdmin ? [{ key: 'actions', header: tx('İşlemler'), render: (agent: Agent) => (
      <div className="flex gap-2">
        {edit({ scope: 'agent', id: agent.agentId }, agent.hostName || agent.agentId, agentLimits.get(agent.agentId))}
        <Button variant="outline" size="sm" onClick={() => setAssigning({ target: { scope: 'agent', id: agent.agentId }, name: agent.hostName || agent.agentId, currentProfileId: agentProfiles.get(agent.agentId) })}>{tx("Profil Ata")}</Button>
      </div>
    ) }] : []),
  ]
  const jobColumns: Column<BackupJob>[] = [
    { key: 'name', header: tx('Backup Job'), wrap: true, render: job => job.name },
    { key: 'agent', header: tx('Agent'), wrap: true, render: job => job.agentName || '—' },
    { key: 'limit', header: tx('Hız sınırı'), wrap: true, render: job => <Limit value={jobLimits.get(job.id)} /> },
    { key: 'profile', header: tx('Bant Genişliği Profili'), wrap: true, render: job => jobProfiles.get(job.id) ? profileMap.get(jobProfiles.get(job.id)!) ?? tx('Atandı') : <span className="text-text-muted">—</span> },
    ...(canAdmin ? [{ key: 'actions', header: tx('İşlemler'), render: (job: BackupJob) => (
      <div className="flex gap-2">
        {edit({ scope: 'job', id: job.id }, job.name, jobLimits.get(job.id))}
        <Button variant="outline" size="sm" onClick={() => setAssigning({ target: { scope: 'job', id: job.id }, name: job.name, currentProfileId: jobProfiles.get(job.id) })}>{tx("Profil Ata")}</Button>
      </div>
    ) }] : []),
  ]
  return <div className="space-y-6">
    <PageHeader title={tx("Upload Bandwidth")} description={tx("{0} · Yedekleme yüklemeleri için bayt/saniye cinsinden hız sınırları.", { "0": tenant.name })} />
    <Notice tone="info">{tx("Atanan profil statik sınırların yerine geçer (iş > ajan > çalışma alanı); pencere dışında sınırsızdır. Profil yoksa statik sınırların en kısıtlayıcısı uygulanır. Duraklatılan aktarımlar manuel devam ettirilir. Geri yükleme/indirme, checksum ve heartbeat sınırlanmaz.")}</Notice>
    {!canAdmin && <Notice tone="warning">{tx("Hız sınırlarını yalnızca Owner / Admin değiştirebilir.")}</Notice>}
    <Tabs label={tx("Hız sınırı kapsamı")} idPrefix="bandwidth" value={tab} onChange={setTab} items={[{ value: 'tenant', label: tx('Tenant limiti') }, { value: 'agents', label: tx('Agent\'lar') }, { value: 'jobs', label: tx('Backup Job\'lar') }, { value: 'profiles', label: tx('Profiller') }]} />
    <div role="tabpanel" id={`bandwidth-panel-${tab}`} aria-labelledby={`bandwidth-tab-${tab}`}>
      <QueryState isPending={limits.isPending} error={limits.error}>
        {tab === 'tenant' && <Section title={tx("Tenant sınırı")} description={tx("Tenant'ın tüm eş zamanlı yüklemelerinin toplamı için üst sınır.")} actions={
          <div className="flex gap-2">
            {edit({ scope: 'tenant' }, tenant.name, data?.tenant.bytesPerSecond)}
            {canAdmin && <Button variant="outline" size="sm" onClick={() => setAssigning({ target: { scope: 'tenant' }, name: tenant.name, currentProfileId: data?.tenant.profileId })}>{tx("Profil Ata")}</Button>}
          </div>
        }>
          <div className="space-y-2">
            <p className="text-xl font-semibold text-text-heading" data-testid="tenant-limit"><Limit value={data?.tenant.bytesPerSecond} /></p>
            <p className="text-sm text-text-muted">{tx("Atanan profil: {0}", { "0": data?.tenant.profileId ? (profileMap.get(data.tenant.profileId) ?? tx("Profil")) : tx("Yok") })}</p>
          </div>
        </Section>}
        {tab === 'agents' && <QueryState isPending={agents.isPending} error={agents.error}>
          <h2 className="mb-4 text-base font-semibold text-text-heading">{tx("Agent sınırları")}</h2>
          <SearchableTable rows={agents.data ?? []} columns={agentColumns} keyExtractor={agent => agent.agentId} text={agent => agent.hostName || agent.agentId} searchLabel="Agent ara" emptyMessage={tx("Agent bulunamadı.")} />
        </QueryState>}
        {tab === 'jobs' && <QueryState isPending={jobs.isPending} error={jobs.error}>
          <h2 className="mb-4 text-base font-semibold text-text-heading">{tx("Backup Job sınırları")}</h2>
          <SearchableTable rows={jobs.data ?? []} columns={jobColumns} keyExtractor={job => job.id} text={job => `${job.name} ${job.agentName ?? ''}`} searchLabel="Backup Job ara" emptyMessage={tx("Backup Job bulunamadı.")} />
        </QueryState>}
        {tab === 'profiles' && <QueryState isPending={profiles.isPending} error={profiles.error}>
          <div className="flex items-center justify-between mb-4">
            <h2 className="text-base font-semibold text-text-heading">{tx("Bant Genişliği Profilleri")}</h2>
            {canAdmin && <Button size="sm" onClick={() => setEditingProfile('new')}>{tx("Yeni Profil")}</Button>}
          </div>
          {profileList.length === 0 ? <div className="rounded-xl border border-border bg-surface"><EmptyState title={tx("Henüz tanımlı profil yok.")} /></div> : (
            <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
              {profileList.map(p => (
                <div key={p.id} className="rounded-xl border border-border bg-surface p-4 space-y-3">
                  <div className="flex items-start justify-between">
                    <div>
                      <h3 className="font-semibold text-text-heading">{p.name}</h3>
                      <p className="text-xs text-text-muted">{p.timeZoneId}</p>
                    </div>
                    {canAdmin && (
                      <div className="flex gap-1">
                        <Button variant="outline" size="sm" onClick={() => setEditingProfile(p)}>{tx("Düzenle")}</Button>
                        <Button variant="outline" size="sm" onClick={() => deleteProfile.mutate(p.id)}>{tx("Sil")}</Button>
                      </div>
                    )}
                  </div>
                  <div className="text-xs text-text-muted space-y-1">
                    <p className="font-medium text-text">{tx("{0} zaman penceresi", { "0": p.windows.length })}</p>
                    {p.windows.slice(0, 3).map((w, idx) => (
                      <p key={idx}>{w.startTime.slice(0, 5)} - {w.endTime.slice(0, 5)}: {w.bytesPerSecond === 0 ? tx("Aktarımı Duraklat") : formatBandwidth(w.bytesPerSecond)}</p>
                    ))}
                    {p.windows.length > 3 && <p>+{p.windows.length - 3} {tx("daha")}</p>}
                  </div>
                </div>
              ))}
            </div>
          )}
        </QueryState>}
      </QueryState>
    </div>
    {editing && data && <LimitDialog {...editing} minimum={data.minimumBytesPerSecond} maximum={data.maximumBytesPerSecond} onClose={() => setEditing(null)} />}
    {assigning && <AssignProfileDialog {...assigning} onClose={() => setAssigning(null)} />}
    {editingProfile && data && <ProfileDialog profile={editingProfile === 'new' ? null : editingProfile} minimum={data.minimumBytesPerSecond} maximum={data.maximumBytesPerSecond} onClose={() => setEditingProfile(null)} />}
  </div>
}
