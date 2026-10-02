import { ArrowLeft } from 'lucide-react'
import { Link, useParams } from 'react-router-dom'
import { SqlInstanceList } from '@/components/sql/sql-instance-list'
import { Skeleton } from '@/components/ui/skeleton'
import { StatusBadge } from '@/components/ui/badge'
import { useClient } from '@/features/agents/hooks'
import { useAuth } from '@/features/auth/use-auth'
import { isAdministrator } from '@/lib/ownership'
import { ROUTES } from '@/lib/routes'
import type { Client } from '@/lib/types'

function AgentInfoCard({ client }: { client: Client }) {
  return (
    <div className="rounded-lg border border-border bg-surface p-4">
      <h2 className="mb-3 text-sm font-medium text-text-muted">Agent Bilgisi</h2>
      <dl className="space-y-2 text-sm">
        <div className="flex justify-between gap-4">
          <dt className="whitespace-nowrap text-text-muted">Versiyon</dt>
          <dd className="min-w-0 break-words text-right font-mono text-text">{client.agent.version}</dd>
        </div>
        <div className="flex justify-between gap-4">
          <dt className="whitespace-nowrap text-text-muted">Durum</dt>
          <dd className="min-w-0 text-right">
            <StatusBadge status={client.agent.status} />
          </dd>
        </div>
        <div className="flex justify-between gap-4">
          <dt className="whitespace-nowrap text-text-muted">Son Heartbeat</dt>
          <dd className="min-w-0 break-words text-right font-mono text-text">
            {new Date(client.agent.lastHeartbeat).toLocaleString('tr-TR')}
          </dd>
        </div>
      </dl>
    </div>
  )
}

function SystemInfoCard({ client }: { client: Client }) {
  return (
    <div className="rounded-lg border border-border bg-surface p-4">
      <h2 className="mb-3 text-sm font-medium text-text-muted">Sistem Bilgisi</h2>
      <dl className="space-y-2 text-sm">
        <div className="flex justify-between gap-4">
          <dt className="whitespace-nowrap text-text-muted">Hostname</dt>
          <dd className="min-w-0 break-words text-right text-text">{client.hostname}</dd>
        </div>
        <div className="flex justify-between gap-4">
          <dt className="whitespace-nowrap text-text-muted">IP</dt>
          <dd className="min-w-0 break-words text-right font-mono text-text">{client.ipAddress}</dd>
        </div>
        <div className="flex justify-between gap-4">
          <dt className="whitespace-nowrap text-text-muted">OS</dt>
          <dd className="min-w-0 break-words text-right text-text">{client.operatingSystem}</dd>
        </div>
      </dl>
    </div>
  )
}

export function ClientDetailPage() {
  const { id } = useParams()
  const { user } = useAuth()
  const { data: client, isLoading, error } = useClient(id ?? '')

  if (!id) {
    return (
      <div className="space-y-4">
        <Link
          to={ROUTES.CLIENTS}
          className="inline-flex items-center gap-1 text-sm font-medium text-primary hover:underline"
        >
          <ArrowLeft className="size-4" />
          Client&apos;lara dön
        </Link>
        <p className="text-text-muted">Client bulunamadı.</p>
      </div>
    )
  }

  if (isLoading) {
    return (
      <div className="space-y-4">
        <Skeleton className="h-8 w-48" />
        <div className="grid gap-4 md:grid-cols-2">
          <Skeleton className="h-32 w-full" />
          <Skeleton className="h-32 w-full" />
        </div>
      </div>
    )
  }

  if (error) {
    return (
      <div className="space-y-4">
        <Link
          to={ROUTES.CLIENTS}
          className="inline-flex items-center gap-1 text-sm font-medium text-primary hover:underline"
        >
          <ArrowLeft className="size-4" />
          Client&apos;lara dön
        </Link>
        <div className="rounded-lg border border-error/30 bg-error-subtle p-4 text-sm text-error">
          Client yüklenirken bir hata oluştu.
        </div>
      </div>
    )
  }

  if (!client) {
    return (
      <div className="space-y-4">
        <Link
          to={ROUTES.CLIENTS}
          className="inline-flex items-center gap-1 text-sm font-medium text-primary hover:underline"
        >
          <ArrowLeft className="size-4" />
          Client&apos;lara dön
        </Link>
        <p className="text-text-muted">Client bulunamadı.</p>
      </div>
    )
  }

  if (!isAdministrator(user) && client.ownerUserId !== user?.id) {
    return (
      <div className="space-y-4">
        <Link
          to={ROUTES.CLIENTS}
          className="inline-flex items-center gap-1 text-sm font-medium text-primary hover:underline"
        >
          <ArrowLeft className="size-4" />
          Client&apos;lara dön
        </Link>
        <p className="text-text-muted">Bu cihaza erişim yetkiniz yok.</p>
      </div>
    )
  }

  return (
    <div className="space-y-4">
      <Link
        to={ROUTES.CLIENTS}
        className="inline-flex items-center gap-1 text-sm font-medium text-primary hover:underline"
      >
        <ArrowLeft className="size-4" />
        Client&apos;lara dön
      </Link>

      <h1 className="text-2xl font-semibold text-text-heading">{client.hostname}</h1>

      <div className="grid gap-4 md:grid-cols-2">
        <AgentInfoCard client={client} />
        <SystemInfoCard client={client} />
      </div>

      <div>
        <h2 className="mb-3 text-lg font-medium text-text-heading">SQL Server Instances</h2>
        <SqlInstanceList instances={client.instances} />
      </div>
    </div>
  )
}
