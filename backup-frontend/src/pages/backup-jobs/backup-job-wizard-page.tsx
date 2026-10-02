import { useMemo, useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { ArrowLeft, ArrowRight, Check } from 'lucide-react'
import { BackupOptions } from '@/components/backup/backup-options'
import { Button } from '@/components/ui/button'
import { useAuth } from '@/features/auth/use-auth'
import { useClients } from '@/features/agents/hooks'
import { useCreateBackupJob } from '@/features/backup-jobs/hooks'
import { visibleClients } from '@/lib/ownership'
import { ROUTES } from '@/lib/routes'
import { mockStorageProviders } from '@/lib/api/mock-data'
import type { BackupType, DatabaseInfo } from '@/lib/types'

const steps = [
  'Client Seç',
  'Kaynak Seç',
  'Depolama Seç',
  'Zamanlama & Retention',
  'Yedekleme Seçenekleri',
  'Gözden Geçir',
] as const

const backupType: BackupType = 'mssql-full'

export function BackupJobWizardPage() {
  const navigate = useNavigate()
  const { id } = useParams()
  const isEdit = Boolean(id)
  const { user } = useAuth()
  const { data: allClients } = useClients()
  const clients = useMemo(() => visibleClients(allClients ?? [], user), [allClients, user])
  const createMutation = useCreateBackupJob()

  const [step, setStep] = useState(0)
  const [clientId, setClientId] = useState('')
  const [instanceId, setInstanceId] = useState('')
  const [databaseName, setDatabaseName] = useState('')
  const [storageProviderId, setStorageProviderId] = useState('')
  const [schedule, setSchedule] = useState('Her gün 02:00')
  const [retentionDays, setRetentionDays] = useState(14)
  const [jobName, setJobName] = useState('')
  const [options, setOptions] = useState({
    compression: true,
    checksum: true,
    encryption: false,
  })

  const selectedClient = useMemo(
    () => clients?.find((client) => client.id === clientId),
    [clients, clientId]
  )

  const selectedInstance = useMemo(
    () => selectedClient?.instances.find((instance) => instance.id === instanceId),
    [selectedClient, instanceId]
  )

  const selectedDatabase: DatabaseInfo | undefined = useMemo(
    () => selectedInstance?.databases.find((database) => database.name === databaseName),
    [selectedInstance, databaseName]
  )

  const canNext = useMemo(() => {
    switch (step) {
      case 0:
        return clientId.length > 0
      case 1:
        return instanceId.length > 0 && databaseName.length > 0
      case 2:
        return storageProviderId.length > 0
      case 3:
        return schedule.trim().length > 0 && retentionDays > 0 && jobName.trim().length > 0
      default:
        return true
    }
  }, [step, clientId, instanceId, databaseName, storageProviderId, schedule, retentionDays, jobName])

  function handleNext() {
    if (step < steps.length - 1) setStep((previous) => previous + 1)
  }

  function handlePrev() {
    if (step > 0) setStep((previous) => previous - 1)
  }

  function handleCreate() {
    createMutation.mutate(
      {
        id: isEdit && id ? id : `job-${Date.now()}`,
        name: jobName.trim(),
        clientId,
        type: backupType,
        source: `${instanceId}/${databaseName}`,
        storageProviderId,
        schedule: schedule.trim(),
        retentionDays,
        enabled: true,
        options: {
          compression: options.compression,
          checksum: options.checksum,
          encryption: options.encryption,
        },
      },
      {
        onSuccess: () => navigate(ROUTES.BACKUP_JOBS),
      }
    )
  }

  return (
    <div className="space-y-6">
      <Link
        to={ROUTES.BACKUP_JOBS}
        className="inline-flex items-center gap-1 text-sm font-medium text-primary hover:underline"
      >
        <ArrowLeft className="size-4" />
        Yedekleme İşlerine dön
      </Link>

      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1 className="text-2xl font-semibold text-text-heading">
          {isEdit ? 'Yedekleme İşini Düzenle' : 'Yeni Yedekleme İşi'}
        </h1>
        <span className="text-sm text-text-muted">
          Adım {step + 1} / {steps.length}: {steps[step]}
        </span>
      </div>

      <div className="flex gap-1">
        {steps.map((_, index) => (
          <div
            key={index}
            className={`h-1 flex-1 rounded-full ${index <= step ? 'bg-primary' : 'bg-border'}`}
          />
        ))}
      </div>

      <div className="rounded-lg border border-border bg-surface p-6">
        {step === 0 && (
          <div className="space-y-3">
            <h2 className="text-lg font-medium text-text-heading">Client Seç</h2>
            <p className="text-sm text-text-muted">Yedekleme işinin çalışacağı client&apos;ı seçin.</p>
            <div className="grid gap-2">
              {clients?.map((client) => (
                <label
                  key={client.id}
                  className={`flex cursor-pointer items-center justify-between rounded-md border px-4 py-3 ${clientId === client.id ? 'border-primary bg-accent/30' : 'border-border bg-surface'}`}
                >
                  <span className="text-sm font-medium text-text-heading">{client.hostname}</span>
                  <input
                    type="radio"
                    name="client"
                    checked={clientId === client.id}
                    onChange={() => setClientId(client.id)}
                    className="size-4"
                  />
                </label>
              ))}
            </div>
          </div>
        )}

        {step === 1 && (
          <div className="space-y-4">
            <h2 className="text-lg font-medium text-text-heading">Kaynak Seç</h2>
            <p className="text-sm text-text-muted">
              MSSQL Full backup alınır. Kaynak, API üzerinden gelen Server → Database Server → Database listesinden seçilir.
            </p>
            <div className="grid gap-4 md:grid-cols-2">
              <div className="space-y-2">
                <label className="text-sm font-medium text-text-heading">SQL Server Instance</label>
                <select
                  value={instanceId}
                  onChange={(event) => {
                    setInstanceId(event.target.value)
                    setDatabaseName('')
                  }}
                  className="h-9 w-full rounded-md border border-border bg-surface-2 px-3 text-sm outline-none focus:border-ring"
                >
                  <option value="">Instance seçin</option>
                  {selectedClient?.instances.map((instance) => (
                    <option key={instance.id} value={instance.id}>
                      {instance.name} — {instance.version}
                    </option>
                  ))}
                </select>
              </div>
              <div className="space-y-2">
                <label className="text-sm font-medium text-text-heading">Database</label>
                <select
                  value={databaseName}
                  onChange={(event) => setDatabaseName(event.target.value)}
                  className="h-9 w-full rounded-md border border-border bg-surface-2 px-3 text-sm outline-none focus:border-ring"
                  disabled={!selectedInstance}
                >
                  <option value="">Database seçin</option>
                  {selectedInstance?.databases.map((database) => (
                    <option key={database.name} value={database.name}>
                      {database.name} ({database.recoveryModel})
                    </option>
                  ))}
                </select>
              </div>
            </div>
          </div>
        )}

        {step === 2 && (
          <div className="space-y-3">
            <h2 className="text-lg font-medium text-text-heading">Depolama Seç</h2>
            <div className="grid gap-2">
              {mockStorageProviders.map((provider) => (
                <label
                  key={provider.id}
                  className={`flex cursor-pointer items-center justify-between rounded-md border px-4 py-3 ${storageProviderId === provider.id ? 'border-primary bg-accent/30' : 'border-border bg-surface'}`}
                >
                  <span className="text-sm font-medium text-text-heading">
                    {provider.name} — {provider.type}
                  </span>
                  <input
                    type="radio"
                    name="storage"
                    checked={storageProviderId === provider.id}
                    onChange={() => setStorageProviderId(provider.id)}
                    className="size-4"
                  />
                </label>
              ))}
            </div>
          </div>
        )}

        {step === 3 && (
          <div className="space-y-4">
            <h2 className="text-lg font-medium text-text-heading">Zamanlama & Retention</h2>
            <div className="space-y-2">
              <label className="text-sm font-medium text-text-heading">İş Adı</label>
              <input
                value={jobName}
                onChange={(event) => setJobName(event.target.value)}
                placeholder="ERP_PROD Gece Yedek"
                className="h-9 w-full rounded-md border border-border bg-surface-2 px-3 text-sm outline-none focus:border-ring"
              />
            </div>
            <div className="space-y-2">
              <label className="text-sm font-medium text-text-heading">Zamanlama</label>
              <input
                value={schedule}
                onChange={(event) => setSchedule(event.target.value)}
                placeholder="Her gün 02:00"
                className="h-9 w-full rounded-md border border-border bg-surface-2 px-3 text-sm outline-none focus:border-ring"
              />
            </div>
            <div className="space-y-2">
              <label className="text-sm font-medium text-text-heading">Retention (gün)</label>
              <input
                type="number"
                min={1}
                value={retentionDays}
                onChange={(event) => setRetentionDays(Number(event.target.value))}
                className="h-9 w-full rounded-md border border-border bg-surface-2 px-3 text-sm outline-none focus:border-ring"
              />
            </div>
          </div>
        )}

        {step === 4 && (
          <div className="space-y-3">
            <h2 className="text-lg font-medium text-text-heading">Yedekleme Seçenekleri</h2>
            <p className="text-sm text-text-muted">
              Seçenekler seçili instance&apos;ın yeteneklerine göre uyarlanır.
            </p>
            {selectedInstance && selectedDatabase ? (
              <BackupOptions
                capabilities={selectedInstance.capabilities}
                value={options}
                onChange={setOptions}
              />
            ) : (
              <div className="rounded-md border border-warning/30 bg-warning/10 p-3 text-sm text-text">
                Seçenekleri görmek için önce 2. adımda bir instance ve database seçin. Şimdilik
                varsayılan yeteneklerle önizleme gösteriliyor.
              </div>
            )}
            {!selectedInstance && !selectedDatabase && (
              <BackupOptions
                capabilities={{
                  fullBackup: true,
                  differentialBackup: true,
                  transactionLogBackup: true,
                  compression: true,
                  checksum: true,
                  backupEncryption: false,
                  copyOnly: true,
                }}
                value={options}
                onChange={setOptions}
              />
            )}
          </div>
        )}

        {step === 5 && (
          <div className="space-y-4">
            <h2 className="text-lg font-medium text-text-heading">Gözden Geçir</h2>
            <dl className="space-y-2 text-sm">
              <div className="flex justify-between gap-4">
                <dt className="text-text-muted">Client</dt>
                <dd className="font-medium text-text-heading">{selectedClient?.hostname ?? clientId}</dd>
              </div>
              <div className="flex justify-between gap-4">
                <dt className="text-text-muted">Tip</dt>
                <dd className="text-text">MSSQL Full</dd>
              </div>
              <div className="flex justify-between gap-4">
                <dt className="text-text-muted">Kaynak</dt>
                <dd className="text-text">{`${instanceId}/${databaseName}`}</dd>
              </div>
              <div className="flex justify-between gap-4">
                <dt className="text-text-muted">Depolama</dt>
                <dd className="text-text">{storageProviderId}</dd>
              </div>
              <div className="flex justify-between gap-4">
                <dt className="text-text-muted">Zamanlama</dt>
                <dd className="text-text">{schedule}</dd>
              </div>
              <div className="flex justify-between gap-4">
                <dt className="text-text-muted">Retention</dt>
                <dd className="text-text">{retentionDays} gün</dd>
              </div>
              <div className="flex justify-between gap-4">
                <dt className="text-text-muted">İş Adı</dt>
                <dd className="text-text">{jobName}</dd>
              </div>
            </dl>
          </div>
        )}
      </div>

      <div className="flex justify-between">
        <Button variant="outline" onClick={handlePrev} disabled={step === 0}>
          <ArrowLeft className="size-4" />
          Geri
        </Button>
        {step < steps.length - 1 ? (
          <Button onClick={handleNext} disabled={!canNext}>
            İleri
            <ArrowRight className="size-4" />
          </Button>
        ) : (
          <Button onClick={handleCreate} disabled={createMutation.isPending || !jobName.trim()}>
            <Check className="size-4" />
            {createMutation.isPending ? 'Kaydediliyor...' : isEdit ? 'Güncelle' : 'Oluştur'}
          </Button>
        )}
      </div>
    </div>
  )
}
