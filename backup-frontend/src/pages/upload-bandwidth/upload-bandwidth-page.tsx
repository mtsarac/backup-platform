import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { DataList } from '@/components/data/data-list'
import type { Column } from '@/components/data/data-list'
import { QueryState } from '@/components/data/query-state'
import { Button } from '@/components/ui/button'
import { EmptyState } from '@/components/ui/empty-state'
import { Notice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { Section } from '@/components/ui/section'
import { Tabs } from '@/components/ui/tabs'
import { TechnicalDetails } from '@/components/ui/technical-details'
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
import { DAYS, describeSchedule } from '@/features/upload-bandwidth/schedule'
import { formatBandwidth, formatExactRate } from '@/features/upload-bandwidth/units'

type Tab = 'tenant' | 'agents' | 'jobs' | 'profiles'

interface Editing { target: BandwidthTarget; name: string; current: number | null }

function Limit({ value }: { value: number | null | undefined }) {
  useLanguage()
  return value == null ? <span className="text-text-muted">{tx("Limitsiz")}</span> : <span>{formatBandwidth(value)}</span>
}

/** Exact values belong to the technical details; a target without a limit has none. */
const exactLimit = (label: string, value: number | null | undefined) => value == null ? [] : [{ label, value: formatExactRate(value) }]

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
    { key: 'name', header: tx('Yedekleme bilgisayarı'), wrap: true, render: agent => agent.hostName || agent.agentId },
    { key: 'limit', header: tx('Hız sınırı'), wrap: true, render: agent => <Limit value={agentLimits.get(agent.agentId)} /> },
    { key: 'profile', header: tx('Bant Genişliği Profili'), wrap: true, render: agent => agentProfiles.get(agent.agentId) ? profileMap.get(agentProfiles.get(agent.agentId)!) ?? tx('Atandı') : <span className="text-text-muted">—</span> },
    ...(canAdmin ? [{ key: 'actions', card: 'actions' as const, header: tx('İşlemler'), render: (agent: Agent) => (
      <div className="flex gap-2">
        {edit({ scope: 'agent', id: agent.agentId }, agent.hostName || agent.agentId, agentLimits.get(agent.agentId))}
        <Button variant="outline" size="sm" onClick={() => setAssigning({ target: { scope: 'agent', id: agent.agentId }, name: agent.hostName || agent.agentId, currentProfileId: agentProfiles.get(agent.agentId) })}>{tx("Profil Ata")}</Button>
      </div>
    ) }] : []),
  ]
  const jobColumns: Column<BackupJob>[] = [
    { key: 'name', header: tx('Yedekleme işi'), wrap: true, render: job => job.name },
    { key: 'agent', header: tx('Yedekleme bilgisayarı'), wrap: true, render: job => job.agentName || '—' },
    { key: 'limit', header: tx('Hız sınırı'), wrap: true, render: job => <Limit value={jobLimits.get(job.id)} /> },
    { key: 'profile', header: tx('Bant Genişliği Profili'), wrap: true, render: job => jobProfiles.get(job.id) ? profileMap.get(jobProfiles.get(job.id)!) ?? tx('Atandı') : <span className="text-text-muted">—</span> },
    ...(canAdmin ? [{ key: 'actions', card: 'actions' as const, header: tx('İşlemler'), render: (job: BackupJob) => (
      <div className="flex gap-2">
        {edit({ scope: 'job', id: job.id }, job.name, jobLimits.get(job.id))}
        <Button variant="outline" size="sm" onClick={() => setAssigning({ target: { scope: 'job', id: job.id }, name: job.name, currentProfileId: jobProfiles.get(job.id) })}>{tx("Profil Ata")}</Button>
      </div>
    ) }] : []),
  ]
  const newProfile = canAdmin ? <Button size="sm" onClick={() => setEditingProfile('new')}>{tx("Yeni Profil")}</Button> : null
  return <div className="space-y-6">
    <PageHeader title={tx("Yükleme bant genişliği")} description={tx("{0} · Yedeklerin ne kadar hızlı yükleneceğini sınırlayın.", { "0": tenant.name })} />
    <Section title={tx("Hangi sınır geçerli?")}>
      <ul className="list-disc space-y-2 pl-5 text-sm text-text">
        <li>{tx("Profil atanmamışsa, yedeklemeye uygulanan sınırların en düşüğü geçerlidir: yedekleme işi, yedekleme bilgisayarı ve çalışma alanı sınırları birbirini aşamaz.")}</li>
        <li>{tx("Bir profil (saate göre değişen sınırlar) atarsanız sabit sınırların yerine geçer. Önce yedekleme işinin, sonra yedekleme bilgisayarının, en son çalışma alanının profili kullanılır. Profildeki saatlerin dışında hız sınırsızdır.")}</li>
        <li>{tx("Profilde duraklatılan aktarımlar kendiliğinden devam etmez; elle devam ettirmeniz gerekir.")}</li>
        <li>{tx("Sınırlanmayanlar: geri yükleme ve indirmeler, dosya doğrulama ve yedekleme bilgisayarlarının bağlantı kontrolleri.")}</li>
      </ul>
    </Section>
    {!canAdmin && <Notice tone="warning">{tx("Hız sınırlarını yalnızca yöneticiler değiştirebilir. Yöneticinize danışın.")}</Notice>}
    <Tabs label={tx("Hız sınırı kapsamı")} idPrefix="bandwidth" value={tab} onChange={setTab} items={[{ value: 'tenant', label: tx('Çalışma alanı') }, { value: 'agents', label: tx('Yedekleme bilgisayarları') }, { value: 'jobs', label: tx('Yedekleme işleri') }, { value: 'profiles', label: tx('Profiller') }]} />
    <div role="tabpanel" id={`bandwidth-panel-${tab}`} aria-labelledby={`bandwidth-tab-${tab}`}>
      <QueryState isPending={limits.isPending} error={limits.error}>
        {tab === 'tenant' && <div className="space-y-4">
          <Section title={tx("Çalışma alanı sınırı")} description={tx("Çalışma alanındaki tüm yüklemelerin toplamı için üst sınır.")} actions={
            <div className="flex gap-2">
              {edit({ scope: 'tenant' }, tenant.name, data?.tenant.bytesPerSecond)}
              {canAdmin && <Button variant="outline" size="sm" onClick={() => setAssigning({ target: { scope: 'tenant' }, name: tenant.name, currentProfileId: data?.tenant.profileId })}>{tx("Profil Ata")}</Button>}
            </div>
          }>
            <div className="space-y-2">
              <p className="text-xl font-semibold text-text-heading" data-testid="tenant-limit"><Limit value={data?.tenant.bytesPerSecond} /></p>
              <p className="text-sm text-text-muted">{tx("Atanan profil: {0}", { "0": data?.tenant.profileId ? (profileMap.get(data.tenant.profileId) ?? tx("Profil")) : tx("Yok") })}</p>
            </div>
          </Section>
          <TechnicalDetails items={exactLimit(tx("Çalışma alanı sınırı"), data?.tenant.bytesPerSecond)} />
        </div>}
        {tab === 'agents' && <QueryState isPending={agents.isPending} error={agents.error}>
          <div className="space-y-4">
            <h2 className="text-base font-semibold text-text-heading">{tx("Yedekleme bilgisayarı sınırları")}</h2>
            <DataList rows={agents.data ?? []} columns={agentColumns} keyExtractor={agent => agent.agentId} search={{ label: tx("Yedekleme bilgisayarı ara"), text: agent => agent.hostName || agent.agentId }} emptyMessage={tx("Yedekleme bilgisayarı bulunamadı.")} />
            <TechnicalDetails items={(agents.data ?? []).flatMap(agent => exactLimit(agent.hostName || agent.agentId, agentLimits.get(agent.agentId)))} />
          </div>
        </QueryState>}
        {tab === 'jobs' && <QueryState isPending={jobs.isPending} error={jobs.error}>
          <div className="space-y-4">
            <h2 className="text-base font-semibold text-text-heading">{tx("Yedekleme işi sınırları")}</h2>
            <DataList rows={jobs.data ?? []} columns={jobColumns} keyExtractor={job => job.id} search={{ label: tx("Yedekleme işi ara"), text: job => `${job.name} ${job.agentName ?? ''}` }} emptyMessage={tx("Yedekleme işi bulunamadı.")} />
            <TechnicalDetails items={(jobs.data ?? []).flatMap(job => exactLimit(job.name, jobLimits.get(job.id)))} />
          </div>
        </QueryState>}
        {tab === 'profiles' && <QueryState isPending={profiles.isPending} error={profiles.error}>
          <div className="space-y-4">
            <div className="flex items-center justify-between">
              <h2 className="text-base font-semibold text-text-heading">{tx("Bant Genişliği Profilleri")}</h2>
              {profileList.length > 0 && newProfile}
            </div>
            {profileList.length === 0 ? <div className="rounded-xl border border-border bg-surface"><EmptyState message={tx("Henüz profil yok. Saate göre değişen bir hız sınırı için profil oluşturun.")} action={newProfile} canAct={canAdmin} /></div> : (
              <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
                {profileList.map(p => (
                  <div key={p.id} className="space-y-3 rounded-xl border border-border bg-surface p-4 shadow-card">
                    <div className="flex items-start justify-between gap-2">
                      <h3 className="min-w-0 break-words font-semibold text-text-heading">{p.name}</h3>
                      {canAdmin && (
                        <div className="flex gap-1">
                          <Button variant="outline" size="sm" onClick={() => setEditingProfile(p)}>{tx("Düzenle")}</Button>
                          <Button variant="outline" size="sm" onClick={() => deleteProfile.mutate(p.id)}>{tx("Sil")}</Button>
                        </div>
                      )}
                    </div>
                    {p.windows.length === 0 ? <p className="text-sm text-text-muted">{tx("Zaman penceresi tanımlı değil.")}</p> : <>
                      <ul className="space-y-1 text-sm text-text">{describeSchedule(p.windows).map(line => <li key={line}>{line}</li>)}</ul>
                      <p className="text-sm text-text-muted">{tx("Diğer saatlerde sınırsız.")}</p>
                    </>}
                  </div>
                ))}
              </div>
            )}
            <TechnicalDetails items={profileList.flatMap(p => [
              { label: tx("{0} · Kimlik", { "0": p.name }), value: p.id },
              { label: tx("{0} · Zaman dilimi", { "0": p.name }), value: p.timeZoneId },
              ...p.windows.map(w => ({
                label: tx("{0} · {1} {2}–{3}", { "0": p.name, "1": DAYS.find(d => d.value === w.dayOfWeek)?.label() ?? w.dayOfWeek, "2": w.startTime.slice(0, 5), "3": w.endTime.slice(0, 5) }),
                value: formatExactRate(w.bytesPerSecond),
              })),
            ])} />
          </div>
        </QueryState>}
      </QueryState>
    </div>
    {editing && data && <LimitDialog {...editing} minimum={data.minimumBytesPerSecond} maximum={data.maximumBytesPerSecond} onClose={() => setEditing(null)} />}
    {assigning && <AssignProfileDialog {...assigning} onClose={() => setAssigning(null)} />}
    {editingProfile && data && <ProfileDialog profile={editingProfile === 'new' ? null : editingProfile} minimum={data.minimumBytesPerSecond} maximum={data.maximumBytesPerSecond} onClose={() => setEditingProfile(null)} />}
  </div>
}
