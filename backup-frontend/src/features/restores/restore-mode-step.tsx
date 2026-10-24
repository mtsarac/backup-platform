import { tx, useLanguage } from '@/lib/i18n'
import { Button } from '@/components/ui/button'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { Section } from '@/components/ui/section'
import { inputClass, selectClass } from '@/components/ui/form-styles'
import type { RestoreMode, RestoreOptions, RestoreView } from './types'
import { errorMessage } from '@/lib/api/failure-messages'

interface RestoreModeStepProps {
  instancePrefix: string
  isPg: boolean
  engineName: string
  mode: RestoreMode
  targetDatabaseName: string
  databaseNameValid: boolean
  confirmOverwriteInput: string
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
  replaceConfirmed: boolean
  onSelectNewDatabase: () => void
  onSelectReplaceExisting: () => void
  onRefreshDiscovery: () => void
  changeDatabaseName: (value: string) => void
  changeConfirmOverwrite: (value: string) => void
  changeOptions: (value: RestoreOptions) => void
  onBack: () => void
  onRunPrecheck: () => void
}

export function RestoreModeStep({ instancePrefix, isPg, engineName, mode, targetDatabaseName, databaseNameValid, confirmOverwriteInput, options, discoveryData, discoveryError, discoveryQueryError, currentDiscovery, discoveryRetryable, discoveryStarting, discoveryScanning, precheckError, precheckRetryable, precheckStarting, restoreStarting, eligibleForRestore, targetValid, replaceConfirmed, onSelectNewDatabase, onSelectReplaceExisting, onRefreshDiscovery, changeDatabaseName, changeConfirmOverwrite, changeOptions, onBack, onRunPrecheck }: RestoreModeStepProps) {
  useLanguage()
  return (
    <div className="space-y-5">
      <Section title={tx("Geri Yükleme Modu ve Hedef Veritabanı")} description={tx("Güvenli yeni bir veritabanı oluşturun veya mevcut olanın üzerine yazın.")}>
        <div className="space-y-4">
          <div className="grid gap-3 sm:grid-cols-2">
            <button
              type="button"
              aria-pressed={mode === 'NewDatabase'}
              className={`rounded-lg border-2 p-4 text-left transition-colors ${mode === 'NewDatabase' ? 'border-primary bg-primary/5 ring-1 ring-primary' : 'border-border bg-surface'}`}
              onClick={onSelectNewDatabase}
            >
              <p className="font-semibold text-text-heading">{tx("Yeni bir veritabanı olarak geri yükle (Önerilen · Güvenli)")}</p>
              <p className="mt-1 text-base text-text-muted">{tx("Mevcut veritabanlarına asla dokunmaz. Belirleyeceğiniz yeni bir adla sıfırdan veritabanı açar.")}</p>
            </button>

            <button
              type="button"
              aria-pressed={mode === 'ReplaceExistingDatabase'}
              className={`rounded-lg border-2 p-4 text-left transition-colors ${mode === 'ReplaceExistingDatabase' ? 'border-error bg-error-subtle ring-1 ring-error' : 'border-border bg-surface'}`}
              disabled={discoveryStarting}
              onClick={onSelectReplaceExisting}
            >
              <p className="font-semibold text-error">{tx("Mevcut veritabanının üzerine yaz (Riskli)")}</p>
              <p className="mt-1 text-base text-text-muted">
                {isPg ? tx('Hedef Database değiştirilir; eski Database bp_old_* adıyla kurtarma için korunur. Operatör temizliği gerekir.') : tx('Hedef sunucudaki mevcut veritabanının üzerine yazar. Eski veriler kalıcı olarak kaybolur!')}
              </p>
            </button>
          </div>

          {mode === 'NewDatabase' && (
            <div className="space-y-2">
              <label htmlFor={`${instancePrefix}-new-dbname`} className="block text-sm font-medium">{tx("Yeni Veritabanı Adı")}</label>
              <input
                id={`${instancePrefix}-new-dbname`}
                className={inputClass}
                value={targetDatabaseName}
                onChange={(e) => changeDatabaseName(e.target.value)}
                placeholder={tx("Yeni veritabanı adı")}
              />
              <p className="text-xs text-text-muted">{tx("Bu ad hedef")}{' '}{engineName}{' '}{tx("sunucusunda henüz var olmamalıdır.")}{isPg && tx(' En fazla 63 UTF-8 byte.')}</p>
              {!databaseNameValid && isPg && <p role="alert" className="text-xs text-error">{tx("PostgreSQL Database adı zorunludur ve en fazla 63 UTF-8 byte olabilir.")}</p>}
            </div>
          )}

          {mode === 'ReplaceExistingDatabase' && (
            <div className="space-y-4 rounded-xl border border-error/40 bg-surface p-4">
              <Notice tone="warning" title={tx("DİKKAT: Veri Kaybı Riski!")}>
                {isPg ? tx('Hedef Database değiştirilecektir. Eski Database bağlantıları kapalı bp_old_* adıyla korunur; kurtarma ve temizlik operatörün sorumluluğundadır.') : tx('Bu işlem hedef veritabanının içeriğini tamamen silecektir. İşlem geri alınamaz.')}
              </Notice>

              <div className="space-y-2">
                <div className="flex flex-wrap items-center justify-between gap-2">
                  <label htmlFor={`${instancePrefix}-replace-db-select`} className="block text-sm font-medium">{tx("Üzerine Yazılacak Veritabanı")}</label>
                  <Button variant="outline" size="sm" onClick={() => onRefreshDiscovery()} disabled={discoveryScanning}>
                    {discoveryScanning ? tx('Taranıyor…') : discoveryRetryable ? tx('Tekrar Dene') : tx('Sunucudan Yenile')}
                  </Button>
                </div>

                {discoveryData?.result?.discovery ? (
                  <select
                    id={`${instancePrefix}-replace-db-select`}
                    className={`${selectClass} w-full`}
                    value={targetDatabaseName}
                    onChange={(e) => changeDatabaseName(e.target.value)}
                  >
                    <option value="">{tx("Bir veritabanı seçin")}</option>
                    {discoveryData.result.discovery.databases.map((db) => (
                      <option key={db.name} value={db.name} disabled={db.isSystem}>
                        {db.name} {db.isSystem ? tx('(Sistem DB - seçilemez)') : `(${db.availability})`}
                      </option>
                    ))}
                  </select>
                ) : (
                  <input
                    id={`${instancePrefix}-replace-db-select`}
                    className={inputClass}
                    value={targetDatabaseName}
                    onChange={(e) => changeDatabaseName(e.target.value)}
                    placeholder={tx("Üzerine yazılacak veritabanı adı")}
                  />
                )}
                {(discoveryError != null || (currentDiscovery && discoveryQueryError)) && (
                  <div role="alert" className="text-sm text-error">{errorMessage(discoveryError || discoveryQueryError, 'restore')}</div>
                )}
              </div>

              <div className="space-y-2">
                <label htmlFor={`${instancePrefix}-confirm-overwrite`} className="block break-words text-sm font-medium text-error">{tx("Onaylamak için veritabanı adını (")}<span className="break-all font-mono font-bold">{targetDatabaseName}</span>{tx(") birebir yazın:")}</label>
                <input
                  id={`${instancePrefix}-confirm-overwrite`}
                  className={`${inputClass} border-error/50`}
                  value={confirmOverwriteInput}
                  onChange={(e) => changeConfirmOverwrite(e.target.value)}
                  placeholder={targetDatabaseName}
                />
                {confirmOverwriteInput.length > 0 && confirmOverwriteInput !== targetDatabaseName && (
                  <p className="text-sm text-error">{tx("Girdiğiniz ad hedef veritabanı adı ile eşleşmiyor.")}</p>
                )}
              </div>
            </div>
          )}

          {/* Advanced Options */}
          <div className="space-y-2 pt-2 border-t border-border">
            <p className="text-sm font-medium text-text-heading">{tx("Seçenekler")}</p>
            <div className="grid gap-2 sm:grid-cols-2 text-sm">
              <label className="flex items-center gap-2">
                <input
                  type="checkbox"
                  checked={options.verifyChecksum ?? true}
                  onChange={(e) => changeOptions({ ...options, verifyChecksum: e.target.checked })}
                />
                <span>{tx("SHA-256 Checksum Doğrula")}</span>
              </label>
              <label className="flex items-center gap-2">
                <input
                  type="checkbox"
                  checked={options.verifyBackup ?? true}
                  onChange={(e) => changeOptions({ ...options, verifyBackup: e.target.checked })}
                />
                <span>{isPg ? tx('pg_restore Arşiv Doğrulaması') : tx('RESTORE VERIFYONLY Çalıştır')}</span>
              </label>
              {mode === 'ReplaceExistingDatabase' && (
                <label className="flex items-center gap-2 text-error sm:col-span-2">
                  <input
                    type="checkbox"
                    checked={options.terminateActiveConnections ?? false}
                    onChange={(e) => changeOptions({ ...options, terminateActiveConnections: e.target.checked })}
                  />
                  <span>{isPg ? tx('Mevcut aktif bağlantıları sonlandır (pg_terminate_backend)') : tx('Mevcut aktif bağlantıları sonlandır (WITH ROLLBACK IMMEDIATE)')}</span>
                </label>
              )}
            </div>
          </div>
        </div>
      </Section>

      {precheckError != null && (
        <ErrorNotice error={precheckError} scope="restore" />
      )}

      <div className="flex flex-wrap justify-between gap-3">
        <Button variant="outline" onClick={() => onBack()}>{tx("← Geri")}</Button>
        <Button
          disabled={!eligibleForRestore || !targetValid || !databaseNameValid || !replaceConfirmed || precheckStarting || restoreStarting}
          onClick={() => onRunPrecheck()}
        >
          {precheckStarting ? tx('Ön Kontrol Başlatılıyor…') : precheckRetryable ? tx('Tekrar Dene') : tx('Ön Kontrolü Çalıştır →')}
        </Button>
      </div>
    </div>
  )
}
