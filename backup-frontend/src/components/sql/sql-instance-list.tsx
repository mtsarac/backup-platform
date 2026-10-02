import { ChevronDown, Server } from 'lucide-react'
import { DataTable } from '@/components/data/data-table'
import type { Column } from '@/components/data/data-table'
import { Badge } from '@/components/ui/badge'
import type { DatabaseInfo, SqlCapability, SqlInstance } from '@/lib/types'

const capabilityItems = [
  { key: 'fullBackup', label: 'Full Backup' },
  { key: 'differentialBackup', label: 'Differential Backup' },
  { key: 'transactionLogBackup', label: 'Transaction Log Backup' },
  { key: 'compression', label: 'Compression' },
  { key: 'checksum', label: 'Checksum' },
  { key: 'backupEncryption', label: 'Backup Encryption' },
  { key: 'copyOnly', label: 'COPY_ONLY' },
] satisfies { key: keyof SqlCapability; label: string }[]

const databaseColumns: Column<DatabaseInfo>[] = [
  { key: 'name', header: 'Database', render: (database) => database.name },
  { key: 'state', header: 'Durum', render: (database) => database.state },
  {
    key: 'recoveryModel',
    header: 'Recovery Model',
    render: (database) => database.recoveryModel,
  },
  {
    key: 'compatibilityLevel',
    header: 'Compatibility Level',
    render: (database) => database.compatibilityLevel,
  },
]

export function SqlInstanceList({ instances }: { instances: readonly SqlInstance[] }) {
  if (instances.length === 0) {
    return (
      <p className="text-sm text-text-muted">Kayıtlı SQL Server Instance bulunamadı.</p>
    )
  }

  return (
    <div className="space-y-4">
      {instances.map((instance) => (
        <details
          key={instance.id}
          className="group overflow-hidden rounded-lg border border-border bg-surface"
        >
          <summary className="flex cursor-pointer list-none items-center justify-between border-b border-border bg-surface-2 px-4 py-3 outline-none focus-visible:ring-2 focus-visible:ring-ring">
            <div className="flex items-center gap-2">
              <Server className="size-4 text-primary" />
              <h3 className="font-medium text-text-heading">{instance.name}</h3>
              <Badge tone="neutral">{instance.productLevel}</Badge>
            </div>
            <div className="flex items-center gap-3">
              <span className="text-sm text-text-muted">
                {instance.version} · {instance.edition}
              </span>
              <ChevronDown className="size-4 text-text-muted transition-transform group-open:rotate-180" />
            </div>
          </summary>
          <div className="space-y-4 p-4">
            <div>
              <h4 className="mb-2 text-sm font-medium text-text-heading">Yetenekler</h4>
              <div className="flex flex-wrap gap-2">
                {capabilityItems.map(({ key, label }) => (
                  <Badge key={key} tone={instance.capabilities[key] ? 'success' : 'neutral'}>
                    {label}
                  </Badge>
                ))}
              </div>
            </div>
            <div>
              <h4 className="mb-2 text-sm font-medium text-text-heading">Databases</h4>
              {instance.databases.length === 0 ? (
                <p className="text-sm text-text-muted">Database bulunamadı.</p>
              ) : (
                <DataTable
                  rows={instance.databases}
                  columns={databaseColumns}
                  keyExtractor={(database) => database.name}
                />
              )}
            </div>
          </div>
        </details>
      ))}
    </div>
  )
}
