import { useMemo, useState } from 'react'
import { ArrowLeft } from 'lucide-react'
import { Link, useParams } from 'react-router-dom'
import { CompatibilityIndicator } from '@/components/backup/compatibility-indicator'
import type { CompatibilityStatus } from '@/components/backup/compatibility-indicator'
import { Button } from '@/components/ui/button'
import { Skeleton } from '@/components/ui/skeleton'
import { useClients } from '@/features/agents/hooks'
import { useAuth } from '@/features/auth/use-auth'
import { useBackup } from '@/features/backups/hooks'
import { isAdministrator, visibleClients } from '@/lib/ownership'
import { ROUTES } from '@/lib/routes'

function backupDetailPath(id: string) {
  return ROUTES.BACKUP_DETAIL.replace(':id', id)
}

function majorVersion(version?: string): number {
  const [major] = version?.split('.') ?? []
  return Number.parseInt(major ?? '0', 10)
}

function compatibility(sourceVersion?: string, targetVersion?: string): {
  status: CompatibilityStatus
  message: string
} {
  if (!targetVersion) {
    return { status: 'warning', message: 'Restore hedefi seçildiğinde uyumluluk hesaplanır.' }
  }

  if (majorVersion(targetVersion) < majorVersion(sourceVersion)) {
    return {
      status: 'unsupported',
      message: 'Hedef SQL Server sürümü kaynak backup sürümünden düşük olduğu için restore desteklenmiyor.',
    }
  }

  return { status: 'compatible', message: 'Hedef SQL Server sürümü bu backup için uygundur.' }
}

export function RestorePage() {
  const { id } = useParams()
  const { user } = useAuth()
  const { data: backup, isLoading, error } = useBackup(id ?? '')
  const { data: allClients } = useClients()
  const clients = useMemo(() => visibleClients(allClients ?? [], user), [allClients, user])
  const [clientId, setClientId] = useState('')
  const [instanceId, setInstanceId] = useState('')
  const [targetDatabase, setTargetDatabase] = useState('')

  const selectedClient = useMemo(
    () => clients?.find((client) => client.id === clientId),
    [clients, clientId]
  )
  const selectedInstance = useMemo(
    () => selectedClient?.instances.find((instance) => instance.id === instanceId),
    [selectedClient, instanceId]
  )
  const compatibilityResult = compatibility(backup?.sqlVersion, selectedInstance?.version)

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
        to={id ? backupDetailPath(id) : ROUTES.BACKUP_HISTORY}
        className="inline-flex items-center gap-1 text-sm font-medium text-primary hover:underline"
      >
        <ArrowLeft className="size-4" />
        Backup detayına dön
      </Link>

      <h1 className="text-2xl font-semibold text-text-heading">Restore</h1>

      {error ? (
        <div className="rounded-lg border border-error/30 bg-error-subtle p-4 text-sm text-error">
          Backup bilgisi yüklenirken bir hata oluştu.
        </div>
      ) : backup && (isAdministrator(user) || clients.some((client) => client.id === backup.clientId)) ? (
        <div className="grid gap-4 lg:grid-cols-[1fr_20rem]">
          <div className="space-y-4">
            <div className="rounded-lg border border-border bg-surface p-4">
              <h2 className="mb-3 text-sm font-medium text-text-heading">Kaynak Backup</h2>
              <dl className="space-y-2 text-sm">
                <div className="flex justify-between gap-4">
                  <dt className="text-text-muted">SQL Server Version</dt>
                  <dd className="font-mono text-text">{backup.sqlVersion ?? '-'}</dd>
                </div>
                <div className="flex justify-between gap-4">
                  <dt className="text-text-muted">Database</dt>
                  <dd className="text-text">{backup.databaseName ?? '-'}</dd>
                </div>
                <div className="flex justify-between gap-4">
                  <dt className="text-text-muted">Recovery Model</dt>
                  <dd className="text-text">{backup.recoveryModel ?? '-'}</dd>
                </div>
              </dl>
            </div>

            <div className="rounded-lg border border-border bg-surface p-4">
              <h2 className="mb-3 text-sm font-medium text-text-heading">Restore Hedefi</h2>
              <div className="grid gap-4 md:grid-cols-2">
                <label className="space-y-2 text-sm">
                  <span className="font-medium text-text-heading">Client</span>
                  <select
                    value={clientId}
                    onChange={(event) => {
                      setClientId(event.target.value)
                      setInstanceId('')
                    }}
                    className="h-9 w-full rounded-md border border-border bg-surface-2 px-3 outline-none focus:border-ring"
                  >
                    <option value="">Client seçin</option>
                    {clients?.map((client) => (
                      <option key={client.id} value={client.id}>
                        {client.hostname}
                      </option>
                    ))}
                  </select>
                </label>
                <label className="space-y-2 text-sm">
                  <span className="font-medium text-text-heading">SQL Server Instance</span>
                  <select
                    value={instanceId}
                    onChange={(event) => setInstanceId(event.target.value)}
                    disabled={!selectedClient}
                    className="h-9 w-full rounded-md border border-border bg-surface-2 px-3 outline-none focus:border-ring"
                  >
                    <option value="">Instance seçin</option>
                    {selectedClient?.instances.map((instance) => (
                      <option key={instance.id} value={instance.id}>
                        {instance.name} - {instance.version}
                      </option>
                    ))}
                  </select>
                </label>
                <label className="space-y-2 text-sm md:col-span-2">
                  <span className="font-medium text-text-heading">Hedef Database Adı</span>
                  <input
                    value={targetDatabase}
                    onChange={(event) => setTargetDatabase(event.target.value)}
                    placeholder={backup.databaseName ?? 'RESTORE_DB'}
                    className="h-9 w-full rounded-md border border-border bg-surface-2 px-3 outline-none focus:border-ring"
                  />
                </label>
              </div>
            </div>
          </div>

          <div className="space-y-4">
            <CompatibilityIndicator {...compatibilityResult} />
            <Button className="w-full" disabled={compatibilityResult.status !== 'compatible' || targetDatabase.trim().length === 0}>
              Restore Başlat
            </Button>
          </div>
        </div>
      ) : backup ? (
        <p className="text-text-muted">Bu backup&apos;a erişim yetkiniz yok.</p>
      ) : (
        <p className="text-text-muted">Backup bulunamadı.</p>
      )}
    </div>
  )
}
