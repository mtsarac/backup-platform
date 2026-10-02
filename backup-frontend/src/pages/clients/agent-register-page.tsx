import { Download } from 'lucide-react'
import { DataTable } from '@/components/data/data-table'
import type { Column } from '@/components/data/data-table'
import { Button } from '@/components/ui/button'
import { StatusBadge } from '@/components/ui/badge'
import type { AgentStatus } from '@/lib/types'

interface PendingRegistration {
  readonly id: string
  readonly hostname: string
  readonly token: string
  readonly status: AgentStatus
}

const registrationToken = 'backup-reg-token-demo-001'

const pendingRegistrations: readonly PendingRegistration[] = [
  {
    id: 'pending-001',
    hostname: 'ANK-SQL-02',
    token: 'backup-reg-token-ank-002',
    status: 'waiting',
  },
  {
    id: 'pending-002',
    hostname: 'IST-APP-03',
    token: 'backup-reg-token-ist-003',
    status: 'offline',
  },
]

const pendingColumns: Column<PendingRegistration>[] = [
  { key: 'hostname', header: 'Hostname', render: (row) => row.hostname },
  {
    key: 'token',
    header: 'Token',
    render: (row) => (
      <code className="rounded bg-surface-2 px-1.5 py-0.5 font-mono text-xs text-text">
        {row.token}
      </code>
    ),
  },
  {
    key: 'status',
    header: 'Durum',
    render: (row) => <StatusBadge status={row.status} />,
  },
]

export function AgentRegisterPage() {
  return (
    <div className="space-y-4">
      <h1 className="text-2xl font-semibold text-text-heading">Agent Kur</h1>

      <div className="grid gap-4 md:grid-cols-2">
        <div className="rounded-lg border border-border bg-surface p-4">
          <h2 className="mb-3 text-sm font-medium text-text-heading">Installer</h2>
          <p className="mb-4 text-sm text-text-muted">
            Agent installer&apos;ı indirin ve hedef Client üzerinde çalıştırın.
          </p>
          <Button disabled>
            <Download className="size-4" />
            Installer İndir
          </Button>
        </div>

        <div className="rounded-lg border border-border bg-surface p-4">
          <h2 className="mb-3 text-sm font-medium text-text-heading">Kayıt Token&apos;ı</h2>
          <p className="mb-2 text-sm text-text-muted">
            Agent kurulumu sırasında aşağıdaki token&apos;ı kullanın.
          </p>
          <code className="block rounded bg-surface-2 p-3 font-mono text-sm text-text">
            {registrationToken}
          </code>
        </div>
      </div>

      <div className="rounded-lg border border-border bg-surface p-4">
        <h2 className="mb-3 text-sm font-medium text-text-heading">Kurulum Adımları</h2>
        <ol className="list-inside list-decimal space-y-1 text-sm text-text">
          <li>Installer&apos;ı indirin.</li>
          <li>Hedef Windows Server üzerinde yönetici olarak çalıştırın.</li>
          <li>Kurulum sihirbazında kayıt token&apos;ını girin.</li>
          <li>Agent bağlandığında Client listesinde görünecektir.</li>
          <li>Kaydolan cihaz hesabınıza bağlanır; admin tüm cihazları ve sahiplerini görür.</li>
        </ol>
      </div>

      <div>
        <h2 className="mb-3 text-lg font-medium text-text-heading">Bekleyen Kayıtlar</h2>
        <DataTable
          rows={pendingRegistrations}
          columns={pendingColumns}
          keyExtractor={(row) => row.id}
          emptyMessage="Bekleyen kayıt bulunamadı."
        />
      </div>
    </div>
  )
}
