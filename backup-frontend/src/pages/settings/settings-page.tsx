import { useState } from 'react'
import { Navigate } from 'react-router-dom'
import { Button } from '@/components/ui/button'
import { useAuth } from '@/features/auth/use-auth'
import { isAdministrator } from '@/lib/ownership'
import { ROUTES } from '@/lib/routes'
import { mockSettings } from '@/lib/api/mock-data'

export function SettingsPage() {
  const { user } = useAuth()
  const [settings, setSettings] = useState(mockSettings)

  if (!isAdministrator(user)) {
    return <Navigate to={ROUTES.DASHBOARD} replace />
  }

  return (
    <div className="space-y-6">
      <h1 className="text-2xl font-semibold text-text-heading">Ayarlar</h1>

      <div className="max-w-xl space-y-4 rounded-lg border border-border bg-surface p-4">
        <div className="space-y-2">
          <label
            htmlFor="retention"
            className="text-sm font-medium text-text-heading"
          >
            Varsayılan Retention (gün)
          </label>
          <input
            id="retention"
            type="number"
            value={settings.defaultRetentionDays}
            onChange={(e) =>
              setSettings((s) => ({
                ...s,
                defaultRetentionDays: Number(e.target.value),
              }))
            }
            className="h-9 w-full rounded-md border border-border bg-surface-2 px-3 text-sm outline-none focus:border-ring"
          />
        </div>

        <div className="space-y-2">
          <label
            htmlFor="storage"
            className="text-sm font-medium text-text-heading"
          >
            Depolama Uyarı Eşiği (%)
          </label>
          <input
            id="storage"
            type="number"
            min={0}
            max={100}
            value={settings.storageWarningPercent}
            onChange={(e) =>
              setSettings((s) => ({
                ...s,
                storageWarningPercent: Number(e.target.value),
              }))
            }
            className="h-9 w-full rounded-md border border-border bg-surface-2 px-3 text-sm outline-none focus:border-ring"
          />
        </div>

        <div className="space-y-2">
          <label
            htmlFor="offline"
            className="text-sm font-medium text-text-heading"
          >
            Offline Agent Zaman Aşımı (dakika)
          </label>
          <input
            id="offline"
            type="number"
            value={settings.offlineAgentTimeoutMinutes}
            onChange={(e) =>
              setSettings((s) => ({
                ...s,
                offlineAgentTimeoutMinutes: Number(e.target.value),
              }))
            }
            className="h-9 w-full rounded-md border border-border bg-surface-2 px-3 text-sm outline-none focus:border-ring"
          />
        </div>

        <div className="flex items-center gap-3">
          <input
            id="notify"
            type="checkbox"
            checked={settings.notifyOnFailure}
            onChange={(e) =>
              setSettings((s) => ({ ...s, notifyOnFailure: e.target.checked }))
            }
            className="size-4"
          />
          <label htmlFor="notify" className="text-sm text-text-heading">
            Başarısız yedekleme bildirimleri gönder
          </label>
        </div>

        <Button
          type="button"
          onClick={() => alert('Ayarlar kaydedildi (mock).')}
        >
          Kaydet
        </Button>
      </div>
    </div>
  )
}
