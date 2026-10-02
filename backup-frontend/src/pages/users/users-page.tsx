import { DataTable, type Column } from '@/components/data/data-table'
import { Skeleton } from '@/components/ui/skeleton'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { useAuth } from '@/features/auth/use-auth'
import { useUpdateUserStatus, useUsers } from '@/features/users/hooks'
import { isAdministrator } from '@/lib/ownership'
import { ROUTES } from '@/lib/routes'
import { Navigate } from 'react-router-dom'
import type { User } from '@/lib/types'

function roleLabel(role: User['role']): string {
  switch (role) {
    case 'administrator':
      return 'Admin'
    case 'operator':
      return 'Operatör'
    case 'viewer':
      return 'Viewer'
  }
}

export function UsersPage() {
  const { user } = useAuth()
  const { data: users, isLoading, error } = useUsers()
  const updateStatus = useUpdateUserStatus()

  if (!isAdministrator(user)) {
    return <Navigate to={ROUTES.DASHBOARD} replace />
  }

  const columns: Column<User>[] = [
    { key: 'username', header: 'Kullanıcı Adı', render: (u) => u.username },
    { key: 'email', header: 'E-posta', render: (u) => u.email },
    { key: 'role', header: 'Rol', render: (u) => roleLabel(u.role) },
    {
      key: 'status',
      header: 'Durum',
      render: (u) => (
        <Badge tone={u.status === 'active' ? 'success' : 'neutral'}>
          {u.status}
        </Badge>
      ),
    },
    {
      key: 'actions',
      header: 'İşlemler',
      render: (u) => (
        <Button
          variant="outline"
          size="sm"
          disabled={updateStatus.isPending}
          onClick={() =>
            updateStatus.mutate({
              id: u.id,
              status: u.status === 'active' ? 'inactive' : 'active',
            })
          }
        >
          {u.status === 'active' ? 'Devre Dışı Bırak' : 'Aktifleştir'}
        </Button>
      ),
    },
  ]

  if (isLoading) {
    return (
      <div className="space-y-4">
        <Skeleton className="h-8 w-40" />
        <Skeleton className="h-64" />
      </div>
    )
  }

  return (
    <div className="space-y-4">
      <h1 className="text-2xl font-semibold text-text-heading">Kullanıcılar</h1>

      {error ? (
        <div className="rounded-lg border border-error/30 bg-error-subtle p-4 text-sm text-error">
          Kullanıcılar yüklenirken bir hata oluştu.
        </div>
      ) : (
        <DataTable
          rows={users ?? []}
          columns={columns}
          keyExtractor={(u) => u.id}
          emptyMessage="Kullanıcı bulunamadı."
        />
      )}
    </div>
  )
}
