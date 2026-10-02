import { useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import { DataTable } from '@/components/data/data-table'
import type { Column } from '@/components/data/data-table'
import { FilterBar } from '@/components/data/filter-bar'
import { ProgressBar } from '@/components/data/progress-bar'
import { Skeleton } from '@/components/ui/skeleton'
import { StatusBadge } from '@/components/ui/badge'
import { useAuth } from '@/features/auth/use-auth'
import { useClients } from '@/features/agents/hooks'
import { useBackups } from '@/features/backups/hooks'
import { mockBackupJobs, mockClients, mockStorageProviders } from '@/lib/api/mock-data'
import { displayOwner, isAdministrator, visibleBackups, visibleClients } from '@/lib/ownership'
import { ROUTES } from '@/lib/routes'
import type { Backup, BackupType } from '@/lib/types'

function backupTypeLabel(type: BackupType): string {
  return type === 'mssql-full' ? 'MSSQL Full' : type
}

function formatBytes(bytes?: number): string {
  if (!bytes) return '-'
  return `${(bytes / 1024 ** 3).toFixed(1)} GB`
}

function formatDuration(seconds?: number): string {
  if (!seconds) return 'Devam ediyor'
  const minutes = Math.floor(seconds / 60)
  const remainder = seconds % 60
  return `${minutes} dk ${remainder} sn`
}

function clientLabel(clientId: string): string {
  return mockClients.find((client) => client.id === clientId)?.hostname ?? clientId
}

function jobLabel(jobId: string): string {
  return mockBackupJobs.find((job) => job.id === jobId)?.name ?? jobId
}

function storageLabel(storageProviderId: string): string {
  return mockStorageProviders.find((provider) => provider.id === storageProviderId)?.name ?? storageProviderId
}

const backupColumns: Column<Backup>[] = [
  { key: 'job', header: 'İş', render: (backup) => jobLabel(backup.jobId) },
  { key: 'client', header: 'Client', render: (backup) => clientLabel(backup.clientId) },
  { key: 'type', header: 'Tip', render: (backup) => backupTypeLabel(backup.type) },
  {
    key: 'startedAt',
    header: 'Başlangıç',
    render: (backup) => new Date(backup.startedAt).toLocaleString('tr-TR'),
  },
  { key: 'duration', header: 'Süre', render: (backup) => formatDuration(backup.durationSeconds) },
  { key: 'size', header: 'Boyut', render: (backup) => formatBytes(backup.fileSizeBytes) },
  { key: 'storage', header: 'Depolama', render: (backup) => storageLabel(backup.storageProviderId) },
  {
    key: 'status',
    header: 'Durum',
    render: (backup) => (
      <div className="min-w-32 space-y-1">
        <StatusBadge status={backup.status} />
        {backup.status === 'running' && <ProgressBar indeterminate />}
      </div>
    ),
  },
  {
    key: 'actions',
    header: 'İşlemler',
    render: (backup) => (
      <Link
        to={ROUTES.BACKUP_DETAIL.replace(':id', backup.id)}
        className="text-sm font-medium text-primary hover:underline"
      >
        Detay
      </Link>
    ),
  },
]

export function BackupsPage() {
  const { user } = useAuth()
  const { data: clients } = useClients()
  const { data: backups, isLoading, error } = useBackups()
  const [search, setSearch] = useState('')
  const admin = isAdministrator(user)
  const visible = useMemo(() => visibleClients(clients ?? [], user), [clients, user])

  const columns = useMemo<Column<Backup>[]>(() => {
    if (!admin) return backupColumns
    return [
      ...backupColumns,
      {
        key: 'owner',
        header: 'Sahip',
        render: (backup: Backup) =>
          displayOwner(visible.find((client) => client.id === backup.clientId)?.ownerUserId ?? '-'),
      },
    ]
  }, [admin, visible])

  const filteredBackups = useMemo(() => {
    if (!backups) return []
    const scoped = visibleBackups(backups, visible, user)
    const term = search.trim().toLowerCase()
    if (term.length === 0) return scoped
    return scoped.filter(
      (backup) =>
        jobLabel(backup.jobId).toLowerCase().includes(term) ||
        clientLabel(backup.clientId).toLowerCase().includes(term) ||
        backupTypeLabel(backup.type).toLowerCase().includes(term) ||
        backup.status.toLowerCase().includes(term)
    )
  }, [backups, visible, user, search])

  return (
    <div className="space-y-4">
      <h1 className="text-2xl font-semibold text-text-heading">Yedekleme Geçmişi</h1>

      <FilterBar search={search} onSearchChange={setSearch} placeholder="İş, Client, tip veya durum ara..." />

      {error ? (
        <div className="rounded-lg border border-error/30 bg-error-subtle p-4 text-sm text-error">
          Yedekleme geçmişi yüklenirken bir hata oluştu.
        </div>
      ) : isLoading ? (
        <div className="space-y-2 rounded-lg border border-border bg-surface p-4">
          <Skeleton className="h-8 w-full" />
          <Skeleton className="h-8 w-full" />
          <Skeleton className="h-8 w-full" />
        </div>
      ) : (
        <DataTable
          rows={filteredBackups}
          columns={columns}
          keyExtractor={(backup) => backup.id}
          emptyMessage="Yedekleme kaydı bulunamadı."
        />
      )}
    </div>
  )
}
