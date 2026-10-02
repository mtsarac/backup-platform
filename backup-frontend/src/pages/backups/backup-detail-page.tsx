import { ArrowLeft } from 'lucide-react'
import { Link, useParams } from 'react-router-dom'
import { BackupDetail } from '@/components/backup/backup-detail'
import { Skeleton } from '@/components/ui/skeleton'
import { useClients } from '@/features/agents/hooks'
import { useAuth } from '@/features/auth/use-auth'
import { useBackup } from '@/features/backups/hooks'
import { isAdministrator, visibleClients } from '@/lib/ownership'
import { ROUTES } from '@/lib/routes'

export function BackupDetailPage() {
  const { id } = useParams()
  const { user } = useAuth()
  const { data: backup, isLoading, error } = useBackup(id ?? '')
  const { data: clients } = useClients()
  const visible =
    backup && (isAdministrator(user) || visibleClients(clients ?? [], user).some((client) => client.id === backup.clientId))

  if (isLoading) {
    return (
      <div className="space-y-4">
        <Skeleton className="h-8 w-48" />
        <Skeleton className="h-96 w-full" />
      </div>
    )
  }

  return (
    <div className="space-y-4">
      <Link
        to={ROUTES.BACKUP_HISTORY}
        className="inline-flex items-center gap-1 text-sm font-medium text-primary hover:underline"
      >
        <ArrowLeft className="size-4" />
        Yedekleme Geçmişine dön
      </Link>

      {error ? (
        <div className="rounded-lg border border-error/30 bg-error-subtle p-4 text-sm text-error">
          Backup detayı yüklenirken bir hata oluştu.
        </div>
      ) : backup && visible ? (
        <BackupDetail backup={backup} />
      ) : backup ? (
        <p className="text-text-muted">Bu backup&apos;a erişim yetkiniz yok.</p>
      ) : (
        <p className="text-text-muted">Backup bulunamadı.</p>
      )}
    </div>
  )
}
