import { useMemo } from 'react'
import type { ElementType } from 'react'
import { useClients } from '@/features/agents/hooks'
import { useBackupJobs } from '@/features/backup-jobs/hooks'
import { useBackups } from '@/features/backups/hooks'
import { useStorageProviders } from '@/features/storage/hooks'
import { DataTable } from '@/components/data/data-table'
import type { Column } from '@/components/data/data-table'
import { ProgressBar } from '@/components/data/progress-bar'
import { Skeleton } from '@/components/ui/skeleton'
import { StatusBadge } from '@/components/ui/badge'
import { HardDrive, Monitor, PlayCircle, AlertCircle } from 'lucide-react'
import type { Backup } from '@/lib/types'

function KpiCard({
  label,
  value,
  icon: Icon,
  tone,
}: {
  label: string
  value: number
  icon: ElementType
  tone?: 'neutral' | 'danger'
}) {
  return (
    <div className="rounded-lg border border-border bg-surface p-4">
      <div className="flex items-center justify-between">
        <div>
          <div className="text-sm text-text-muted">{label}</div>
          <div
            className={`text-2xl font-semibold ${tone === 'danger' ? 'text-error' : 'text-text-heading'}`}
          >
            {value}
          </div>
        </div>
        <Icon
          className={`size-8 ${tone === 'danger' ? 'text-error' : 'text-text-muted'}`}
        />
      </div>
    </div>
  )
}

export function DashboardPage() {
  const { data: clients, isLoading: clientsLoading } = useClients()
  const { data: jobs, isLoading: jobsLoading } = useBackupJobs()
  const { data: backups, isLoading: backupsLoading } = useBackups()
  const { data: providers, isLoading: providersLoading } = useStorageProviders()

  const isLoading =
    clientsLoading || jobsLoading || backupsLoading || providersLoading

  const activeClients = (clients ?? []).filter(
    (c) => c.agent.status === 'online',
  ).length
  const totalJobs = (jobs ?? []).length
  const last24h = useMemo(
    () =>
      (backups ?? []).filter(
        (b) =>
          new Date(b.startedAt) > new Date(Date.now() - 24 * 60 * 60 * 1000),
      ),
    [backups],
  )
  const successful24h = last24h.filter((b) => b.status === 'completed').length
  const failed24h = last24h.filter((b) => b.status === 'failed').length
  const runningJobs = (backups ?? []).filter((b) => b.status === 'running')
  const offlineAgents = (clients ?? []).filter(
    (c) => c.agent.status === 'offline',
  )
  const storageWarnings = (providers ?? []).filter(
    (p) => p.totalBytes > 0 && p.usedBytes / p.totalBytes > 0.8,
  )

  const recentColumns: Column<Backup>[] = [
    { key: 'job', header: 'Job', render: (b) => b.jobId },
    { key: 'client', header: 'Client', render: (b) => b.clientId },
    {
      key: 'status',
      header: 'Durum',
      render: (b) => <StatusBadge status={b.status} />,
    },
    {
      key: 'started',
      header: 'Başlangıç',
      render: (b) => new Date(b.startedAt).toLocaleString('tr-TR'),
    },
  ]

  if (isLoading) {
    return (
      <div className="space-y-4">
        <Skeleton className="h-8 w-40" />
        <div className="grid gap-4 md:grid-cols-2 lg:grid-cols-4">
          <Skeleton className="h-24" />
          <Skeleton className="h-24" />
          <Skeleton className="h-24" />
          <Skeleton className="h-24" />
        </div>
      </div>
    )
  }

  return (
    <div className="space-y-6">
      <h1 className="text-2xl font-semibold text-text-heading">Panel</h1>

      <div className="grid gap-4 md:grid-cols-2 lg:grid-cols-4">
        <KpiCard label="Aktif Client'lar" value={activeClients} icon={Monitor} />
        <KpiCard label="Toplam İş" value={totalJobs} icon={PlayCircle} />
        <KpiCard
          label="Başarılı Yedek (24s)"
          value={successful24h}
          icon={HardDrive}
        />
        <KpiCard
          label="Başarısız Yedek (24s)"
          value={failed24h}
          icon={AlertCircle}
          tone="danger"
        />
      </div>

      <div className="grid gap-4 lg:grid-cols-2">
        <div className="space-y-3 rounded-lg border border-border bg-surface p-4">
          <h2 className="font-medium text-text-heading">Çalışan İşler</h2>
          {runningJobs.length === 0 ? (
            <p className="text-sm text-text-muted">Şu anda çalışan iş yok.</p>
          ) : (
            <ul className="space-y-3">
              {runningJobs.map((job) => (
                <li key={job.id} className="space-y-1">
                  <div className="flex justify-between text-sm">
                    <span className="text-text">{job.jobId}</span>
                    <span className="text-text-muted">{job.clientId}</span>
                  </div>
                  <ProgressBar value={45} />
                </li>
              ))}
            </ul>
          )}
        </div>

        <div className="space-y-3 rounded-lg border border-border bg-surface p-4">
          <h2 className="font-medium text-text-heading">Offline Agent'lar</h2>
          {offlineAgents.length === 0 ? (
            <p className="text-sm text-text-muted">Tüm agent'lar çevrimiçi.</p>
          ) : (
            <ul className="space-y-2 text-sm">
              {offlineAgents.map((client) => (
                <li key={client.id} className="flex justify-between">
                  <span className="text-text">{client.hostname}</span>
                  <span className="text-text-muted">
                    {new Date(client.agent.lastHeartbeat).toLocaleString(
                      'tr-TR',
                    )}
                  </span>
                </li>
              ))}
            </ul>
          )}
        </div>
      </div>

      {storageWarnings.length > 0 && (
        <div className="space-y-3 rounded-lg border border-warning/30 bg-warning/10 p-4">
          <h2 className="font-medium text-text-heading">Depolama Uyarıları</h2>
          <ul className="space-y-2 text-sm">
            {storageWarnings.map((provider) => (
              <li key={provider.id} className="flex justify-between">
                <span className="text-text">{provider.name}</span>
                <span className="text-text-muted">
                  %{Math.round((provider.usedBytes / provider.totalBytes) * 100)}{' '}
                  kullanımda
                </span>
              </li>
            ))}
          </ul>
        </div>
      )}

      <div className="space-y-3">
        <h2 className="font-medium text-text-heading">Son Yedekleme Geçmişi</h2>
        <DataTable
          rows={(backups ?? []).slice(0, 5)}
          columns={recentColumns}
          keyExtractor={(b) => b.id}
          emptyMessage="Henüz yedekleme kaydı yok."
        />
      </div>
    </div>
  )
}
