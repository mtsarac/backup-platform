import { useMemo, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { Plus } from 'lucide-react'
import { DataTable } from '@/components/data/data-table'
import type { Column } from '@/components/data/data-table'
import { FilterBar } from '@/components/data/filter-bar'
import { StorageCapacityBar } from '@/components/storage/storage-capacity-bar'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Skeleton } from '@/components/ui/skeleton'
import { useAuth } from '@/features/auth/use-auth'
import { useClients } from '@/features/agents/hooks'
import { useStorageProviders } from '@/features/storage/hooks'
import { canManageStorage, displayOwner, isAdministrator, visibleStorages } from '@/lib/ownership'
import { ROUTES } from '@/lib/routes'
import type { StorageProvider, StorageType } from '@/lib/types'

function assertNever(value: never): never {
  throw new Error(`Unexpected storage value: ${String(value)}`)
}

function storageTypeLabel(type: StorageType): string {
  switch (type) {
    case 'local':
      return 'Local'
    case 'central':
      return 'Central'
    case 'sftp':
      return 'SFTP'
    case 'ftp':
      return 'FTP'
    case 'google-drive':
      return 'Google Drive'
    case 'nas':
      return 'NAS/SMB'
    default:
      return assertNever(type)
  }
}

function statusBadge(provider: StorageProvider) {
  switch (provider.connectionStatus) {
    case 'connected':
      return <Badge tone="success">Bağlı</Badge>
    case 'disconnected':
      return <Badge tone="error">Bağlı Değil</Badge>
    case 'unknown':
      return <Badge tone="warning">Bilinmiyor</Badge>
    default:
      return assertNever(provider.connectionStatus)
  }
}

const storageColumns: Column<StorageProvider>[] = [
  { key: 'name', header: 'Ad', render: (provider) => provider.name },
  { key: 'type', header: 'Tip', render: (provider) => storageTypeLabel(provider.type) },
  { key: 'status', header: 'Durum', render: statusBadge },
  {
    key: 'capacity',
    header: 'Kapasite',
    render: (provider) => (
      <StorageCapacityBar usedBytes={provider.usedBytes} totalBytes={provider.totalBytes} />
    ),
  },
  {
    key: 'lastUpload',
    header: 'Son Upload',
    render: (provider) =>
      provider.lastSuccessfulUpload ? new Date(provider.lastSuccessfulUpload).toLocaleString('tr-TR') : '-',
  },
]

export function StoragePage() {
  const navigate = useNavigate()
  const { user } = useAuth()
  const { data: clients } = useClients()
  const { data: providers, isLoading, error } = useStorageProviders()
  const [search, setSearch] = useState('')
  const admin = isAdministrator(user)

  const columns = useMemo<Column<StorageProvider>[]>(() => {
    const hostname = (id: string): string =>
      clients?.find((client) => client.id === id)?.hostname ?? id
    const linkedDevices: Column<StorageProvider> = {
      key: 'devices',
      header: 'Bağlı Cihazlar',
      render: (provider) => provider.clientIds.map(hostname).join(', ') || '-',
    }
    const withDevices = [...storageColumns.slice(0, 3), linkedDevices, ...storageColumns.slice(3)]
    if (!admin) return withDevices
    return [
      ...withDevices,
      {
        key: 'owner',
        header: 'Sahip',
        render: (provider: StorageProvider) => displayOwner(provider.ownerUserId),
      },
    ]
  }, [admin, clients])

  const filteredProviders = useMemo(() => {
    if (!providers) return []
    const scoped = visibleStorages(providers, user)
    const term = search.trim().toLowerCase()
    if (term.length === 0) return scoped
    return scoped.filter(
      (provider) =>
        provider.name.toLowerCase().includes(term) ||
        storageTypeLabel(provider.type).toLowerCase().includes(term) ||
        provider.connectionStatus.toLowerCase().includes(term)
    )
  }, [providers, user, search])

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1 className="text-2xl font-semibold text-text-heading">Depolama</h1>
        {canManageStorage(user) && (
          <Button onClick={() => navigate(ROUTES.STORAGE_NEW)}>
            <Plus className="size-4" />
            Depolama Ekle
          </Button>
        )}
      </div>

      <FilterBar search={search} onSearchChange={setSearch} placeholder="Ad, tip veya durum ara..." />

      {error ? (
        <div className="rounded-lg border border-error/30 bg-error-subtle p-4 text-sm text-error">
          Depolama sağlayıcıları yüklenirken bir hata oluştu.
        </div>
      ) : isLoading ? (
        <div className="space-y-2 rounded-lg border border-border bg-surface p-4">
          <Skeleton className="h-8 w-full" />
          <Skeleton className="h-8 w-full" />
          <Skeleton className="h-8 w-full" />
        </div>
      ) : (
        <DataTable
          rows={filteredProviders}
          columns={columns}
          keyExtractor={(provider) => provider.id}
          emptyMessage="Depolama sağlayıcısı bulunamadı."
        />
      )}
    </div>
  )
}
