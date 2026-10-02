import { useMemo, useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { Plus } from 'lucide-react'
import { DataTable } from '@/components/data/data-table'
import type { Column } from '@/components/data/data-table'
import { FilterBar } from '@/components/data/filter-bar'
import { Button } from '@/components/ui/button'
import { Skeleton } from '@/components/ui/skeleton'
import { StatusBadge } from '@/components/ui/badge'
import { useAuth } from '@/features/auth/use-auth'
import { useClients } from '@/features/agents/hooks'
import { displayOwner, isAdministrator, visibleClients } from '@/lib/ownership'
import { ROUTES } from '@/lib/routes'
import type { Client } from '@/lib/types'

function clientDetailPath(id: string) {
  return ROUTES.CLIENT_DETAIL.replace(':id', id)
}

const clientColumns: Column<Client>[] = [
  { key: 'hostname', header: 'Hostname', render: (client) => client.hostname },
  { key: 'ipAddress', header: 'IP', render: (client) => client.ipAddress },
  { key: 'operatingSystem', header: 'OS', render: (client) => client.operatingSystem },
  {
    key: 'status',
    header: 'Durum',
    render: (client) => <StatusBadge status={client.agent.status} />,
  },
  {
    key: 'instanceCount',
    header: 'Instance Sayısı',
    render: (client) => client.instances.length,
  },
  {
    key: 'actions',
    header: 'İşlemler',
    render: (client) => (
      <Link
        to={clientDetailPath(client.id)}
        className="text-sm font-medium text-primary hover:underline"
      >
        Görüntüle
      </Link>
    ),
  },
]

export function ClientsPage() {
  const navigate = useNavigate()
  const { user } = useAuth()
  const { data: clients, isLoading, error } = useClients()
  const [search, setSearch] = useState('')
  const admin = isAdministrator(user)

  const columns = useMemo<Column<Client>[]>(() => {
    if (!admin) return clientColumns
    return [
      ...clientColumns,
      {
        key: 'owner',
        header: 'Sahip',
        render: (client: Client) => displayOwner(client.ownerUserId),
      },
    ]
  }, [admin])

  const filteredClients = useMemo(() => {
    if (!clients) return []
    const scoped = visibleClients(clients, user)
    const term = search.trim().toLowerCase()
    if (term.length === 0) return scoped
    return scoped.filter(
      (client) =>
        client.hostname.toLowerCase().includes(term) ||
        client.ipAddress.toLowerCase().includes(term) ||
        client.operatingSystem.toLowerCase().includes(term)
    )
  }, [clients, user, search])

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1 className="text-2xl font-semibold text-text-heading">Client&apos;lar</h1>
        <Button onClick={() => navigate(ROUTES.CLIENT_REGISTER)}>
          <Plus className="size-4" />
          Agent Kur
        </Button>
      </div>

      <FilterBar
        search={search}
        onSearchChange={setSearch}
        placeholder="Hostname, IP veya OS ara..."
      />

      {error ? (
        <div className="rounded-lg border border-error/30 bg-error-subtle p-4 text-sm text-error">
          Client listesi yüklenirken bir hata oluştu.
        </div>
      ) : isLoading ? (
        <div className="space-y-2 rounded-lg border border-border bg-surface p-4">
          <Skeleton className="h-8 w-full" />
          <Skeleton className="h-8 w-full" />
          <Skeleton className="h-8 w-full" />
        </div>
      ) : (
        <DataTable
          rows={filteredClients}
          columns={columns}
          keyExtractor={(client) => client.id}
          emptyMessage="Client bulunamadı."
        />
      )}
    </div>
  )
}
