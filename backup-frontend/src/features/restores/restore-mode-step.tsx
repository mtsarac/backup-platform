import { useId } from 'react'
import { CheckField } from '@/components/ui/check-field'
import { Card } from '@/components/ui/card'
import { Select, TextInput } from '@/components/ui/text-input'
import { tx, useLanguage } from '@/lib/i18n'
import { Button } from '@/components/ui/button'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { Section } from '@/components/ui/section'
import type { RestoreMode, RestoreOptions, RestoreView } from './types'
import { errorMessage } from '@/lib/api/failure-messages'
import { Disclosure } from '@/components/ui/disclosure'

interface RestoreModeStepProps {
  instancePrefix: string
  isPg: boolean
  mode: RestoreMode
  targetDatabaseName: string
  databaseNameValid: boolean
  options: RestoreOptions
  discoveryData: RestoreView | undefined
  discoveryError: unknown
  discoveryQueryError: Error | null
  currentDiscovery: boolean
  discoveryRetryable: boolean
  discoveryStarting: boolean
  discoveryScanning: boolean
  precheckError: unknown
  precheckRetryable: boolean
  precheckStarting: boolean
  restoreStarting: boolean
  eligibleForRestore: boolean
  targetValid: boolean
  /** A typed database login that is not stored yet: the target is not usable until it is. */
  loginPending: boolean
  onSelectNewDatabase: () => void
  onSelectReplaceExisting: () => void
  onRefreshDiscovery: () => void
  changeDatabaseName: (value: string) => void
  changeOptions: (value: RestoreOptions) => void
  onBack: () => void
  onRunPrecheck: () => void
}

export function RestoreModeStep({ instancePrefix, isPg, mode, targetDatabaseName, databaseNameValid, options, discoveryData, discoveryError, discoveryQueryError, currentDiscovery, discoveryRetryable, discoveryStarting, discoveryScanning, precheckError, precheckRetryable, precheckStarting, restoreStarting, eligibleForRestore, targetValid, loginPending, onSelectNewDatabase, onSelectReplaceExisting, onRefreshDiscovery, changeDatabaseName, changeOptions, onBack, onRunPrecheck }: RestoreModeStepProps) {
  useLanguage()
  const newDescriptionId = useId()
  const replaceDescriptionId = useId()
  return (
    <div className="space-y-5">
      <Section title={tx("Nasıl geri yüklensin?")} description={tx("Neden soruluyor? Yedek ya yeni bir veritabanı olarak açılır ya da var olan bir veritabanının yerine geçer. İkincisi geri alınamaz, bu yüzden ayrıca onay istenir.")}>
        <div className="space-y-4">
          <div className="grid gap-3 sm:grid-cols-2">
            <button
              type="button"
              aria-pressed={mode === 'NewDatabase'}
              aria-describedby={newDescriptionId}
              className={`rounded-lg border-2 p-4 text-left transition-colors ${mode === 'NewDatabase' ? 'border-primary bg-primary/5 ring-1 ring-primary' : 'border-border bg-surface'}`}
              onClick={onSelectNewDatabase}
            >
              <p className="font-semibold text-text-heading">{tx("Yeni bir veritabanı olarak (önerilen)")}</p>
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
              <p className="font-semibold text-error">{tx("Mevcut bir veritabanının yerine (riskli)")}</p>
              <p id={replaceDescriptionId} className="mt-1 text-base text-text-muted">
                {isPg ? tx('Seçtiğiniz veritabanı yedektekiyle değiştirilir.') : tx('Seçtiğiniz veritabanının içeriği yedektekiyle değiştirilir.')}
              </p>
            </button>
          </div>

          {mode === 'NewDatabase' && (
            <div className="space-y-2">
              <label htmlFor={`${instancePrefix}-new-dbname`} className="block text-sm font-medium">{tx("Yeni Veritabanı Adı")}</label>
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
                {isPg ? tx('Seçtiğiniz veritabanı değiştirilecek. Eski veritabanı kenarda saklanır; kurtarma ve temizlik size aittir.') : tx('Seçtiğiniz veritabanının içeriği tamamen silinecek. İşlem geri alınamaz.')}
              </Notice>

              <div className="space-y-2">
                <div className="flex flex-wrap items-center justify-between gap-2">
                  <label htmlFor={`${instancePrefix}-replace-db-select`} className="block text-sm font-medium">{tx("Yerine geçilecek veritabanı")}</label>
                  <Button variant="outline" size="sm" onClick={() => onRefreshDiscovery()} disabled={discoveryScanning || !targetValid || loginPending}>
                    {discoveryScanning ? tx('Taranıyor…') : discoveryRetryable ? tx('Tekrar Dene') : tx('Listeyi yenile')}
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

              <CheckField className="items-start" label={<span>{tx("Açık bağlantıları kapat")}<span className="block text-text-muted">{tx("Veritabanını şu an kullanan bağlantılar varsa önce kapatılır; kapatılmazsa geri yükleme engellenebilir.")}</span></span>} checked={options.terminateActiveConnections ?? false}
                  onChange={(e) => changeOptions({ ...options, terminateActiveConnections: e.target.checked })} />
            </Card>
          )}

          <Disclosure title={tx("Gelişmiş seçenekler")}><div className="grid gap-2 sm:grid-cols-2">
              <CheckField label={tx("Yedek dosyasının bütünlüğünü doğrula (SHA-256)")} checked={options.verifyChecksum ?? true}
                  onChange={(e) => changeOptions({ ...options, verifyChecksum: e.target.checked })} />
              <CheckField label={<>{isPg ? tx('Yedek arşivini doğrula (pg_restore)') : tx('Yedeği geri yüklemeden önce sına (RESTORE VERIFYONLY)')}</>} checked={options.verifyBackup ?? true}
                  onChange={(e) => changeOptions({ ...options, verifyBackup: e.target.checked })} />
            </div></Disclosure>
        </div>
      </Section>

      {precheckError != null && (
        <ErrorNotice error={precheckError} scope="restore" />
      )}

      <div className="flex flex-wrap justify-between gap-3">
        <Button variant="outline" onClick={() => onBack()}>{tx("← Geri")}</Button>
        <Button
          disabled={!eligibleForRestore || !targetValid || loginPending || !databaseNameValid || precheckStarting || restoreStarting}
          onClick={() => onRunPrecheck()}
        >
          {precheckStarting ? tx('Ön Kontrol Başlatılıyor…') : precheckRetryable ? tx('Tekrar Dene') : tx('Ön Kontrolü Çalıştır →')}
        </Button>
      </div>
    </div>
  )
}
