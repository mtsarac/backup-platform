import { useId, type ReactNode } from 'react'
import { Card } from '@/components/ui/card'
import { Select, TextInput } from '@/components/ui/text-input'
import { tx, useLanguage } from '@/lib/i18n'
import { Button } from '@/components/ui/button'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { Section } from '@/components/ui/section'
import { Why } from '@/features/backup-jobs/job-form-fields'
import type { RestoreMode, RestoreView } from './types'
import { describeError, errorMessage } from '@/lib/api/failure-messages'

interface RestoreModeStepProps {
  instancePrefix: string
  isPg: boolean
  mode: RestoreMode
  targetDatabaseName: string
  databaseNameValid: boolean
  discoveryData: RestoreView | undefined
  discoveryError: unknown
  discoveryQueryError: Error | null
  currentDiscovery: boolean
  discoveryRetryable: boolean
  discoveryStarting: boolean
  discoveryScanning: boolean
  precheckError: unknown
  precheckRetryable: boolean
  /** A failed precheck is on screen: only "Yeniden kontrol et" or changing the target moves on; Continue would repeat the same failing check. */
  precheckBlocked: boolean
  precheckStarting: boolean
  restoreStarting: boolean
  eligibleForRestore: boolean
  targetValid: boolean
  /** A typed database login that is not stored yet: the target is not usable until it is. */
  loginPending: boolean
  /** The pre-check running or failing after "Devam et"; empty until then. */
  checkPanel: ReactNode
  onSelectNewDatabase: () => void
  onSelectReplaceExisting: () => void
  onRefreshDiscovery: () => void
  changeDatabaseName: (value: string) => void
  onBack?: () => void
  onContinue: () => void
}

/** Where the backup lands and how; continuing runs the pre-check, and only a passed check opens the confirmation. */
export function RestoreModeStep({ instancePrefix, isPg, mode, targetDatabaseName, databaseNameValid, discoveryData, discoveryError, discoveryQueryError, currentDiscovery, discoveryRetryable, discoveryStarting, discoveryScanning, precheckError, precheckRetryable, precheckBlocked, precheckStarting, restoreStarting, eligibleForRestore, targetValid, loginPending, checkPanel, onSelectNewDatabase, onSelectReplaceExisting, onRefreshDiscovery, changeDatabaseName, onBack, onContinue }: RestoreModeStepProps) {
  useLanguage()
  const newDescriptionId = useId()
  const replaceDescriptionId = useId()
  return (
    <div className="space-y-5">
      <Section title={tx("Nasıl geri yüklensin?")} description={<Why lead={false}>{tx("Yedek ya yeni bir veritabanı olarak açılır ya da var olan bir veritabanının yerine geçer. İkincisi geri alınamaz, bu yüzden ayrıca onay istenir.")}</Why>}>
        <div className="space-y-4">
          <div className="grid gap-3 sm:grid-cols-2">
            <button
              type="button"
              aria-pressed={mode === 'NewDatabase'}
              aria-describedby={newDescriptionId}
              className={`rounded-lg border-2 p-4 text-left transition-colors ${mode === 'NewDatabase' ? 'border-primary bg-accent ring-1 ring-primary' : 'border-border bg-surface'}`}
              onClick={onSelectNewDatabase}
            >
              <p className="font-semibold text-text-heading">{tx("Yeni veritabanı")}</p>
              <p id={newDescriptionId} className="mt-1 text-base text-text-muted">{tx("Mevcut veritabanlarına dokunulmaz. Yedek, vereceğiniz yeni bir adla ayrı bir veritabanı olarak açılır.")}</p>
            </button>

            <button
              type="button"
              aria-pressed={mode === 'ReplaceExistingDatabase'}
              aria-describedby={replaceDescriptionId}
              className={`rounded-lg border-2 p-4 text-left transition-colors ${mode === 'ReplaceExistingDatabase' ? 'border-error bg-error-subtle ring-1 ring-error' : 'border-border bg-surface'}`}
              disabled={discoveryStarting}
              onClick={onSelectReplaceExisting}
            >
              <p className="font-semibold text-error">{tx("Yerine geç")} <span className="text-sm font-normal">({tx("riskli")})</span></p>
              <p id={replaceDescriptionId} className="mt-1 text-base text-text-muted">
                {isPg ? tx('Seçtiğiniz veritabanı yedektekiyle değiştirilir.') : tx('Seçtiğiniz veritabanının içeriği yedektekiyle değiştirilir.')}
              </p>
            </button>
          </div>

          {mode === 'NewDatabase' && (
            <div className="space-y-2">
              <label htmlFor={`${instancePrefix}-new-dbname`} className="block text-sm font-medium">{tx("Yeni veritabanı adı")}<span aria-hidden="true" className="text-error"> *</span></label>
              <TextInput
                id={`${instancePrefix}-new-dbname`}
                value={targetDatabaseName}
                onChange={(e) => changeDatabaseName(e.target.value)}
                placeholder={tx("Yeni veritabanı adı")}
              />
              <p className="text-xs text-text-muted">{isPg ? tx('Bu ad hedef sunucuda henüz var olmamalıdır. En fazla 63 UTF-8 bayt.') : tx('Bu ad hedef sunucuda henüz var olmamalıdır.')}</p>
              {!databaseNameValid && isPg && <p role="alert" className="text-xs text-error">{tx("PostgreSQL veritabanı adı zorunludur ve en fazla 63 UTF-8 bayt olabilir.")}</p>}
            </div>
          )}

          {mode === 'ReplaceExistingDatabase' && (
            <Card tone="danger" className="space-y-4">
              <Notice tone="warning" title={tx("Veri kaybı riski")}>
                <p>{tx("Açık bağlantılar kapatılacak ve mevcut veritabanının üzerine yazılacak.")}</p>
                <p className="mt-1">{isPg ? tx('Seçtiğiniz veritabanı değiştirilecek. Eski veritabanı kenarda saklanır; kurtarma ve temizlik size aittir.') : tx('Seçtiğiniz veritabanının içeriği tamamen silinecek. İşlem geri alınamaz.')}</p>
              </Notice>

              <div className="space-y-2">
                <div className="flex flex-wrap items-center justify-between gap-2">
                  <label htmlFor={`${instancePrefix}-replace-db-select`} className="block text-sm font-medium">{tx("Yerine geçilecek veritabanı")}<span aria-hidden="true" className="text-error"> *</span></label>
                  <Button variant="outline" size="sm" onClick={() => onRefreshDiscovery()} disabled={discoveryScanning || !targetValid || loginPending}>
                    {discoveryScanning ? tx('Taranıyor…') : discoveryRetryable ? tx('Tekrar dene') : tx('Listeyi yenile')}
                  </Button>
                </div>

                {discoveryData?.result?.discovery ? (
                  <Select
                    id={`${instancePrefix}-replace-db-select`}
                    value={targetDatabaseName}
                    onChange={(e) => changeDatabaseName(e.target.value)}
                  >
                    <option value="">{tx("Bir veritabanı seçin")}</option>
                    {discoveryData.result.discovery.databases.map((db) => (
                      <option key={db.name} value={db.name} disabled={db.isSystem}>
                        {db.isSystem ? tx('{0} (sistem veritabanı, seçilemez)', { "0": db.name }) : `${db.name} (${db.availability})`}
                      </option>
                    ))}
                  </Select>
                ) : (
                  <TextInput
                    id={`${instancePrefix}-replace-db-select`}
                    value={targetDatabaseName}
                    onChange={(e) => changeDatabaseName(e.target.value)}
                    placeholder={tx("Yerine geçilecek veritabanının adı")}
                  />
                )}
                {(discoveryError != null || (currentDiscovery && discoveryQueryError)) && (
                  <div role="alert" className="text-sm text-error">{errorMessage(discoveryError || discoveryQueryError, 'restore')}</div>
                )}
              </div>
            </Card>
          )}
        </div>
      </Section>

      {precheckError != null && (
        <ErrorNotice error={precheckError} scope="restore"
          onAction={describeError(precheckError, 'restore').actionKind === 'fixCredentials' ? onBack : undefined}
          onRetry={describeError(precheckError, 'restore').actionKind === 'retry' && eligibleForRestore && targetValid && !loginPending && !precheckStarting && !restoreStarting ? onContinue : undefined} />
      )}

      {checkPanel}

      <div className="flex flex-wrap justify-between gap-3">
        {onBack ? <Button variant="outline" onClick={() => onBack()}>{tx("← Geri")}</Button> : <span />}
        <Button
          disabled={!eligibleForRestore || !targetValid || loginPending || !databaseNameValid || precheckStarting || restoreStarting || precheckBlocked}
          onClick={() => onContinue()}
        >
          {precheckStarting ? tx('Ön Kontrol Başlatılıyor…') : precheckRetryable ? tx('Tekrar dene') : tx('Devam et →')}
        </Button>
      </div>
    </div>
  )
}
