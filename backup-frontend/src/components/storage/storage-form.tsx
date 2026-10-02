import { useState } from 'react'
import { Button } from '@/components/ui/button'
import type { StorageType } from '@/lib/types'

const storageTypes: readonly { value: StorageType; label: string }[] = [
  { value: 'local', label: 'Local Disk' },
  { value: 'central', label: 'Central Server' },
  { value: 'sftp', label: 'SFTP' },
  { value: 'ftp', label: 'FTP' },
  { value: 'google-drive', label: 'Google Drive' },
  { value: 'nas', label: 'NAS/SMB' },
] as const

function Field({ label, name, value, onChange, placeholder }: {
  label: string
  name: string
  value: string
  onChange: (name: string, value: string) => void
  placeholder?: string
}) {
  return (
    <label className="space-y-2 text-sm">
      <span className="font-medium text-text-heading">{label}</span>
      <input
        value={value}
        onChange={(event) => onChange(name, event.target.value)}
        placeholder={placeholder}
        className="h-9 w-full rounded-md border border-border bg-surface-2 px-3 outline-none focus:border-ring"
      />
    </label>
  )
}

function assertNever(value: never): never {
  throw new Error(`Unexpected storage type: ${String(value)}`)
}

function typeFields(type: StorageType, config: Record<string, string>, setConfig: (name: string, value: string) => void) {
  switch (type) {
    case 'local':
    case 'central':
      return <Field label="Path" name="path" value={config.path ?? ''} onChange={setConfig} placeholder="D:\\Backups" />
    case 'sftp':
    case 'ftp':
      return (
        <div className="grid gap-4 md:grid-cols-2">
          <Field label="Host" name="host" value={config.host ?? ''} onChange={setConfig} placeholder="backup.example.com" />
          <Field label="Port" name="port" value={config.port ?? ''} onChange={setConfig} placeholder="22" />
          <Field label="Kullanıcı" name="username" value={config.username ?? ''} onChange={setConfig} />
          <Field label="Remote Path" name="remotePath" value={config.remotePath ?? ''} onChange={setConfig} placeholder="/backups" />
        </div>
      )
    case 'nas':
      return (
        <div className="grid gap-4 md:grid-cols-2">
          <Field label="UNC Path" name="path" value={config.path ?? ''} onChange={setConfig} placeholder="\\\\backup-nas\\sql" />
          <Field label="Domain" name="domain" value={config.domain ?? ''} onChange={setConfig} placeholder="CORP" />
          <Field label="Kullanıcı" name="username" value={config.username ?? ''} onChange={setConfig} />
        </div>
      )
    case 'google-drive':
      return <Field label="Folder ID" name="folderId" value={config.folderId ?? ''} onChange={setConfig} placeholder="Google Drive klasör ID" />
    default:
      return assertNever(type)
  }
}

export function StorageForm() {
  const [name, setName] = useState('')
  const [type, setType] = useState<StorageType>('nas')
  const [config, setConfigState] = useState<Record<string, string>>({})
  const [connectionOk, setConnectionOk] = useState(false)

  function setConfig(key: string, value: string) {
    setConnectionOk(false)
    setConfigState((current) => ({ ...current, [key]: value }))
  }

  return (
    <div className="space-y-4 rounded-lg border border-border bg-surface p-4">
      <div className="grid gap-4 md:grid-cols-2">
        <label className="space-y-2 text-sm">
          <span className="font-medium text-text-heading">Ad</span>
          <input
            value={name}
            onChange={(event) => setName(event.target.value)}
            placeholder="Merkez NAS"
            className="h-9 w-full rounded-md border border-border bg-surface-2 px-3 outline-none focus:border-ring"
          />
        </label>
        <label className="space-y-2 text-sm">
          <span className="font-medium text-text-heading">Tip</span>
          <select
            value={type}
            onChange={(event) => {
              const nextType = storageTypes.find((storageType) => storageType.value === event.target.value)?.value
              if (!nextType) return
              setType(nextType)
              setConfigState({})
              setConnectionOk(false)
            }}
            className="h-9 w-full rounded-md border border-border bg-surface-2 px-3 outline-none focus:border-ring"
          >
            {storageTypes.map((storageType) => (
              <option key={storageType.value} value={storageType.value}>
                {storageType.label}
              </option>
            ))}
          </select>
        </label>
      </div>

      {typeFields(type, config, setConfig)}

      <div className="flex flex-wrap items-center gap-3">
        <Button type="button" variant="outline" onClick={() => setConnectionOk(true)}>
          Bağlantıyı Test Et
        </Button>
        <Button type="button" disabled={!name.trim() || !connectionOk}>
          Kaydet
        </Button>
        {connectionOk && <span className="text-sm text-success">Bağlantı başarılı.</span>}
      </div>
    </div>
  )
}
