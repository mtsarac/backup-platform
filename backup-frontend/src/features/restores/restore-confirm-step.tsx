import { tx, useLanguage } from '@/lib/i18n'
import { QueryState } from '@/components/data/query-state'
import { Button } from '@/components/ui/button'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { Fact, Facts, Section } from '@/components/ui/section'
import { PrecheckResults } from './precheck-results'
import { restoreModeLabel } from './presentation'
import type { BackupArtifact } from '@/features/artifacts/types'
import type { RestoreMode, RestoreTarget, RestoreTargetAgent, RestoreView } from './types'
import { ApiError } from '@/lib/api/client'

interface RestoreConfirmStepProps {
  engineName: string
  isPg: boolean
  artifact: BackupArtifact
  mode: RestoreMode
  targetDatabaseName: string
  activeAgent: RestoreTargetAgent | undefined
  selectedAgentId: string
  effectiveTarget: RestoreTarget
  confirmOverwriteInput: string
  precheckData: RestoreView | undefined
  precheckLoading: boolean
  precheckDisplayError: unknown
  precheckError: unknown
  precheckRetryable: boolean
  precheckFailed: boolean
  precheckPassed: boolean
  precheckStarting: boolean
  restoreStarting: boolean
  executionError: unknown
  executionRetryable: boolean
  eligibleForRestore: boolean
  replaceConfirmed: boolean
  onRunPrecheck: (fresh?: boolean) => void
  onBack: () => void
  onStart: () => void
}

export function RestoreConfirmStep({ engineName, isPg, artifact, mode, targetDatabaseName, activeAgent, selectedAgentId, effectiveTarget, confirmOverwriteInput, precheckData, precheckLoading, precheckDisplayError, precheckError, precheckRetryable, precheckFailed, precheckPassed, precheckStarting, restoreStarting, executionError, executionRetryable, eligibleForRestore, replaceConfirmed, onRunPrecheck, onBack, onStart }: RestoreConfirmStepProps) {
  useLanguage()
  return (
    <div className="space-y-5">
      <Section title={tx("Ön Kontrol (Precheck) Sonuçları")} description={tx("Hedef {0} üzerinde hiçbir değişiklik yapılmadan çalıştırılan uyumluluk ve güvenlik kontrolleri.", { "0": engineName })}>
        <QueryState isPending={precheckLoading && !precheckData} error={precheckDisplayError}>
          {precheckData && <PrecheckResults data={precheckData} failed={precheckFailed} passed={precheckPassed}
            onRecheck={() => onRunPrecheck(true)} recheckDisabled={precheckStarting || restoreStarting} />}
        </QueryState>
        {precheckError != null && (
          <Button variant="outline" size="sm" onClick={() => onRunPrecheck()} disabled={precheckStarting || restoreStarting}>
            {precheckRetryable ? tx('Tekrar Dene') : tx('Yeniden Kontrol Et')}
          </Button>
        )}
      </Section>

      <Section title={tx("Başlatmadan önce")} description={tx("Onayınızdan sonra Agent hedef {0} üzerinde Restore çalıştırır.", { "0": engineName })}>
        <div className="space-y-4">
          <Facts>
            <Fact label={tx("Kaynak Database")}>{artifact.databaseName ?? tx('Bilinmeyen Database')}</Fact>
            <Fact label={tx("Hedef Database")}>{targetDatabaseName}</Fact>
            <Fact label={tx("Agent")}>{activeAgent?.hostName ?? selectedAgentId}</Fact>
            <Fact label={tx("Hedef {0}", { "0": engineName })}>{effectiveTarget.host}{effectiveTarget.port ? `:${effectiveTarget.port}` : ''}</Fact>
            <Fact label={tx("Restore modu")}>{restoreModeLabel(mode)}</Fact>
            <Fact label={tx("Ön kontrol")}>{precheckPassed ? tx('Geçti') : tx('Başarılı ön kontrol bekleniyor')}</Fact>
          </Facts>
          {mode === 'ReplaceExistingDatabase' && <Notice tone="warning" title={tx("Mevcut Database'in üzerine yazılacak")}>
            <p>{isPg ? tx('Mevcut Database bp_old_* adıyla korunur ve doğrulanmış geçici Database hedef adını alır. Eski Database otomatik silinmez; operatör kurtarma ve temizlikten sorumludur.') : <><strong className="break-all">{targetDatabaseName}</strong>{' '}{tx("içindeki eski veriler kalıcı olarak kaybolur. İşlem geri alınamaz; iptal etmek eski içeriği geri getirmez.")}</>}</p>
            <p className="mt-1">{tx("Database adı ile verdiğiniz onay:")}{' '}<code className="break-all">{confirmOverwriteInput}</code></p>
          </Notice>}
        </div>
      </Section>
      {executionError != null && (executionError instanceof ApiError ? <ErrorNotice error={executionError} scope="restore" /> : <ErrorNotice what={tx('Geri yükleme başlatılamadı.')} dataImpact={tx('Geri yüklemenin başlayıp başlamadığını geri yükleme listesinden kontrol edin.')} action={tx('Aynı işlemi tekrar deneyin; yeniden denemeler ikinci bir geri yükleme oluşturmaz.')} />)}

      <div className="flex flex-wrap justify-between gap-3">
        <Button variant="outline" onClick={() => onBack()}>{tx("← Mod Ayarlarına Dön")}</Button>
        <Button
          variant={mode === 'ReplaceExistingDatabase' ? 'destructive' : 'default'}
          disabled={!eligibleForRestore || !precheckPassed || precheckFailed || !replaceConfirmed || precheckStarting || restoreStarting}
          onClick={() => onStart()}
        >
          {restoreStarting ? tx('Geri Yükleme Başlatılıyor…') : executionRetryable ? tx('Tekrar Dene') : tx("{0} Olarak Restore Başlat", { "0": restoreModeLabel(mode) })}
        </Button>
      </div>
    </div>
  )
}
