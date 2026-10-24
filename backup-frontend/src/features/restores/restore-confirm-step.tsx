import { useState } from 'react'
import { tx, useLanguage } from '@/lib/i18n'
import { Button } from '@/components/ui/button'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { Fact, Facts, Section } from '@/components/ui/section'
import { TechnicalDetails } from '@/components/ui/technical-details'
import { describeStartFailure, restoreModeLabel } from './presentation'
import type { BackupArtifact } from '@/features/artifacts/types'
import type { RestoreMode, RestoreTarget, RestoreTargetAgent } from './types'
import { ApiError } from '@/lib/api/client'

interface RestoreConfirmStepProps {
  isPg: boolean
  artifact: BackupArtifact
  mode: RestoreMode
  targetDatabaseName: string
  activeAgent: RestoreTargetAgent | undefined
  selectedAgentId: string
  effectiveTarget: RestoreTarget
  precheckPassed: boolean
  precheckStarting: boolean
  restoreStarting: boolean
  executionError: unknown
  executionRetryable: boolean
  eligibleForRestore: boolean
  onBack: () => void
  onStart: () => void
}

export function RestoreConfirmStep({ isPg, artifact, mode, targetDatabaseName, activeAgent, selectedAgentId, effectiveTarget, precheckPassed, precheckStarting, restoreStarting, executionError, executionRetryable, eligibleForRestore, onBack, onStart }: RestoreConfirmStepProps) {
  useLanguage()
  const [confirmOpen, setConfirmOpen] = useState(false)
  const replacing = mode === 'ReplaceExistingDatabase'
  const databaseName = targetDatabaseName.trim()
  const retryLabel = executionRetryable ? tx('Tekrar dene') : undefined
  const failure = describeStartFailure(executionError)

  function start() {
    if (replacing) setConfirmOpen(true)
    else onStart()
  }

  return (
    <div className="space-y-5">
      <Section title={tx("Onay")} description={tx("Bilgileri kontrol edin. Onayınızdan sonra seçtiğiniz yedekleme bilgisayarı geri yüklemeyi başlatır.")}>
        <div className="space-y-4">
          <Facts>
            <Fact label={tx("Yedeklenen veritabanı")}>{artifact.databaseName ?? tx('Bilinmeyen veritabanı')}</Fact>
            <Fact label={tx("Geri yüklenecek veritabanı")}>{databaseName}</Fact>
            <Fact label={tx("Yedekleme bilgisayarı")}>{activeAgent?.hostName ?? selectedAgentId}</Fact>
            <Fact label={tx("Yöntem")}>{restoreModeLabel(mode)}</Fact>
            <Fact label={tx("Ön kontrol")}>{precheckPassed ? tx('Geçti') : tx('Başarılı ön kontrol bekleniyor')}</Fact>
          </Facts>
          {replacing && <Notice tone="warning" title={tx("Mevcut veritabanının yerine geçilecek")}>
            <p>{isPg
              ? tx('Mevcut veritabanı silinmez; yedek olarak kenara alınır ve yerine doğrulanmış kopya konur. Eski veritabanını temizlemek sizin sorumluluğunuzdadır.')
              : tx('Seçtiğiniz veritabanındaki güncel veriler kalıcı olarak kaybolur. İşlem geri alınamaz; iptal etmek eski içeriği geri getirmez.')}</p>
            <p className="mt-1">{tx("Başlatmadan önce veritabanı adını yazarak onaylamanız istenecek.")}</p>
          </Notice>}
          <TechnicalDetails items={[
            { label: tx('Hedef sunucu'), value: `${effectiveTarget.host}${effectiveTarget.port ? `:${effectiveTarget.port}` : ''}` },
            { label: tx('Yedekleme bilgisayarı kimliği'), value: selectedAgentId },
            { label: tx('Yedek dosyası kimliği'), value: artifact.id },
          ]} />
        </div>
      </Section>
      {failure && !confirmOpen && (failure instanceof ApiError ? <ErrorNotice error={failure} scope="restore" /> : <ErrorNotice {...failure} />)}

      <div className="flex flex-wrap justify-between gap-3">
        <Button variant="outline" onClick={() => onBack()}>{tx("← Geri")}</Button>
        <Button
          variant={replacing ? 'destructive' : 'default'}
          disabled={!eligibleForRestore || !precheckPassed || precheckStarting || restoreStarting}
          onClick={start}
        >
          {restoreStarting ? tx('Geri yükleme başlatılıyor…') : retryLabel ?? tx("Geri yüklemeyi başlat")}
        </Button>
      </div>

      {replacing && <ConfirmDialog
        open={confirmOpen}
        onClose={() => setConfirmOpen(false)}
        title={tx("Mevcut veritabanı değiştirilsin mi?")}
        consequence={{
          what: isPg
            ? tx('{0} veritabanı yedekteki verilerle değiştirilir; eski veritabanı silinmeden kenara alınır.', { "0": databaseName })
            : tx('{0} veritabanındaki güncel veriler silinir ve yedekteki verilerle değiştirilir.', { "0": databaseName }),
          who: tx('Bu veritabanını kullanan herkes ve uygulama etkilenir.'),
          reversible: isPg
            ? tx('Kısmen: eski veritabanı kenarda kalır, ancak geri almak elle yapılır.')
            : tx('Hayır. Eski veriler geri getirilemez.'),
        }}
        name={databaseName}
        pending={restoreStarting}
        error={failure}
        errorScope="restore"
        onConfirm={onStart}
        confirmLabel={retryLabel ?? tx("Üzerine yaz ve geri yükle")}
      />}
    </div>
  )
}
