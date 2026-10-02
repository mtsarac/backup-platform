import { useMemo, useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { Plus } from 'lucide-react'
import { DataTable } from '@/components/data/data-table'
import type { Column } from '@/components/data/data-table'
import { FilterBar } from '@/components/data/filter-bar'
import { Button } from '@/components/ui/button'
import { Skeleton } from '@/components/ui/skeleton'
import { Badge } from '@/components/ui/badge'
import { useAuth } from '@/features/auth/use-auth'
import { useClients } from '@/features/agents/hooks'
import { useBackupJobs } from '@/features/backup-jobs/hooks'
import { displayOwner, isAdministrator, visibleBackupJobs, visibleClients } from '@/lib/ownership'
import { ROUTES } from '@/lib/routes'
import type { BackupJob } from '@/lib/types'

function backupTypeLabel(type: BackupJob['type']): string {
  return type === 'mssql-full' ? 'MSSQL Full' : type
}

const backupJobColumns: Column<BackupJob>[] = [
  { key: 'name', header: 'İş Adı', render: (job) => job.name },
  { key: 'client', header: 'Client', render: (job) => job.clientId },
  { key: 'type', header: 'Tip', render: (job) => backupTypeLabel(job.type) },
  { key: 'schedule', header: 'Zamanlama', render: (job) => job.schedule },
  {
    key: 'enabled',
    header: 'Aktif',
    render: (job) =>
      job.enabled ? (
        <Badge tone="success">Aktif</Badge>
      ) : (
        <Badge tone="neutral">Pasif</Badge>
      ),
  },
  {
    key: 'actions',
    header: 'İşlemler',
    render: (job) => (
      <Link
        to={ROUTES.BACKUP_JOB_EDIT.replace(':id', job.id)}
        className="text-sm font-medium text-primary hover:underline"
      >
        Düzenle
      </Link>
    ),
  },
]

export function BackupJobsPage() {
  const navigate = useNavigate()
  const { user } = useAuth()
  const { data: clients } = useClients()
  const { data: jobs, isLoading, error } = useBackupJobs()
  const [search, setSearch] = useState('')
  const admin = isAdministrator(user)
  const visible = useMemo(() => visibleClients(clients ?? [], user), [clients, user])

  const columns = useMemo<Column<BackupJob>[]>(() => {
    if (!admin) return backupJobColumns
    return [
      ...backupJobColumns,
      {
        key: 'owner',
        header: 'Sahip',
        render: (job: BackupJob) =>
          displayOwner(visible.find((client) => client.id === job.clientId)?.ownerUserId ?? '-'),
      },
    ]
  }, [admin, visible])

  const filteredJobs = useMemo(() => {
    if (!jobs) return []
    const scoped = visibleBackupJobs(jobs, visible, user)
    const term = search.trim().toLowerCase()
    if (term.length === 0) return scoped
    return scoped.filter(
      (job) =>
        job.name.toLowerCase().includes(term) ||
        job.clientId.toLowerCase().includes(term) ||
        job.type.toLowerCase().includes(term)
    )
  }, [jobs, visible, user, search])

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1 className="text-2xl font-semibold text-text-heading">Yedekleme İşleri</h1>
        <Button onClick={() => navigate(ROUTES.BACKUP_JOB_NEW)}>
          <Plus className="size-4" />
          Yeni İş
        </Button>
      </div>

      <FilterBar search={search} onSearchChange={setSearch} placeholder="İş adı, Client veya tip ara..." />

      {error ? (
        <div className="rounded-lg border border-error/30 bg-error-subtle p-4 text-sm text-error">
          Yedekleme işleri yüklenirken bir hata oluştu.
        </div>
      ) : isLoading ? (
        <div className="space-y-2 rounded-lg border border-border bg-surface p-4">
          <Skeleton className="h-8 w-full" />
          <Skeleton className="h-8 w-full" />
          <Skeleton className="h-8 w-full" />
        </div>
      ) : (
        <DataTable
          rows={filteredJobs}
          columns={columns}
          keyExtractor={(job) => job.id}
          emptyMessage="Yedekleme işi bulunamadı."
        />
      )}
    </div>
  )
}
