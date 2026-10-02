import type { SqlCapability } from '@/lib/types'

interface BackupOptionsProps {
  capabilities: SqlCapability
  value?: {
    compression: boolean
    checksum: boolean
    encryption: boolean
  }
  onChange?: (next: NonNullable<BackupOptionsProps['value']>) => void
}

interface OptionConfig {
  key: keyof NonNullable<BackupOptionsProps['value']>
  label: string
  getDisabled: (caps: SqlCapability) => { disabled: boolean; reason?: string }
}

const options: readonly OptionConfig[] = [
  {
    key: 'compression',
    label: 'Compression',
    getDisabled: (caps) => ({
      disabled: !caps.compression,
      reason: !caps.compression ? 'Bu SQL Server sürümünde sıkıştırma desteklenmiyor.' : undefined,
    }),
  },
  {
    key: 'checksum',
    label: 'Checksum',
    getDisabled: (caps) => ({
      disabled: !caps.checksum,
      reason: !caps.checksum ? 'Bu SQL Server sürümünde checksum desteklenmiyor.' : undefined,
    }),
  },
  {
    key: 'encryption',
    label: 'Encryption',
    getDisabled: (caps) => ({
      disabled: !caps.backupEncryption,
      reason: !caps.backupEncryption ? 'Bu SQL Server sürümünde şifreleme desteklenmiyor.' : undefined,
    }),
  },
]

export function BackupOptions({ capabilities, value, onChange }: BackupOptionsProps) {
  const current: NonNullable<BackupOptionsProps['value']> = value ?? {
    compression: false,
    checksum: false,
    encryption: false,
  }

  return (
    <div className="space-y-3">
      {options.map((option) => {
        const { disabled, reason } = option.getDisabled(capabilities)
        const checked = Boolean(current[option.key])

        return (
          <label
            key={option.key}
            className={`flex items-start gap-3 rounded-md border px-3 py-2 ${disabled ? 'border-border bg-surface-2 opacity-60' : 'border-border bg-surface'}`}
            title={reason}
          >
            <input
              type="checkbox"
              checked={checked}
              disabled={disabled}
              onChange={(event) => {
                onChange?.({ ...current, [option.key]: event.target.checked })
              }}
              className="mt-0.5 size-4 rounded border-border"
            />
            <span className="flex-1">
              <span className="text-sm font-medium text-text-heading">{option.label}</span>
              {reason && <span className="block text-xs text-text-muted">{reason}</span>}
            </span>
          </label>
        )
      })}
    </div>
  )
}
