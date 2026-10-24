import { useState } from 'react'
import { tx, useLanguage } from '@/lib/i18n'
import { Button } from '@/components/ui/button'
import { TypedConfirmDialog } from '@/components/ui/confirm-dialog'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { Fact, Facts, Section } from '@/components/ui/section'
import { TechnicalDetails } from '@/components/ui/technical-details'
import { ApiError } from '@/lib/api/client'
import type { BackupArtifact } from '@/features/artifacts/types'
import { describeStartFailure } from './presentation'
import type { RestoreTargetAgent } from './types'

interface FileRestoreConfirmStepProps {
  artifact: BackupArtifact
  agent: RestoreTargetAgent | undefined
  agentId: string
  original: boolean
  targetReference: string
  relativeDirectory: string
  scope: 'all' | 'selected'
  paths: string[]
  overwrite: boolean
  precheckPassed: boolean
  precheckStarting: boolean
  restoreStarting: boolean
  executionError: unknown
  executionRetryable: boolean
  eligible: boolean
  onBack: () => void
  onStart: () => void
}

export function FileRestoreConfirmStep({ artifact, agent, agentId, original, targetReference, relativeDirectory, scope, paths, overwrite, precheckPassed, precheckStarting, restoreStarting, executionError, executionRetryable, eligible, onBack, onStart }: FileRestoreConfirmStepProps) {
  useLanguage()
  const [confirmOpen, setConfirmOpen] = useState(false)
  const retryLabel = executionRetryable ? tx('Tekrar dene') : undefined
  const failure = describeStartFailure(executionError)

  return (
    <div className="space-y-5">
      <Section title={tx("Onay")} description={tx("Bilgileri kontrol edin. Onayınızdan sonra seçtiğiniz yedekleme bilgisayarı geri yüklemeyi başlatır.")}>
        <div className="space-y-4">
          <Facts>
            <Fact label={tx("Yedeklenen dosya kaynağı")}>{artifact.databaseName ?? tx('Bilinmeyen dosya kaynağı')}</Fact>
            <Fact label={tx("Yedekleme bilgisayarı")}>{agent?.hostName ?? agentId}</Fact>
            <Fact label={tx("Geri yükleme konumu")}>{original ? tx('Özgün konum') : tx('Alternatif konum')}</Fact>
            <Fact label={tx("Hedef dosya kaynağı referansı")}>{targetReference}</Fact>
            <Fact label={tx("Hedef göreli klasör")}>{relativeDirectory || '.'}</Fact>
            <Fact label={tx("Geri yükleme kapsamı")}>{scope === 'all' ? tx('Tüm dosya ve klasörler') : paths.join(', ')}</Fact>
            <Fact label={tx("Mevcut dosyalar")}>{overwrite ? tx('Üzerine yazılır') : tx('Üzerine yazılmaz')}</Fact>
            <Fact label={tx("Ön kontrol")}>{precheckPassed ? tx('Geçti') : tx('Başarılı ön kontrol bekleniyor')}</Fact>
          </Facts>
          {overwrite && <Notice tone="warning" title={tx("Mevcut dosyaların üzerine yazılacak")}>
            <p>{tx("Seçili hedefteki çakışan dosyalar değiştirilecektir. Başlatmadan önce kaynak referansını, göreli klasörü ve kapsamı kontrol edin.")}</p>
            <p className="mt-1">{tx("Başlatmadan önce hedef dosya kaynağı referansını yazarak onaylamanız istenecek.")}</p>
          </Notice>}
          <TechnicalDetails items={[
            { label: tx('Yedekleme bilgisayarı kimliği'), value: agentId },
            { label: tx('Yedek dosyası kimliği'), value: artifact.id },
            { label: tx('Dosya ve manifest bütünlüğü (SHA-256)'), value: tx('Zorunlu') },
          ]} />
        </div>
      </Section>
      {failure && !confirmOpen && (failure instanceof ApiError ? <ErrorNotice error={failure} scope="restore" /> : <ErrorNotice {...failure} />)}

      <div className="flex flex-wrap justify-between gap-3">
        <Button variant="outline" onClick={() => onBack()}>{tx("← Geri")}</Button>
        <Button variant={overwrite ? 'destructive' : 'default'} disabled={!eligible || !precheckPassed || precheckStarting || restoreStarting}
          onClick={() => (overwrite ? setConfirmOpen(true) : onStart())}>
          {restoreStarting ? tx('Geri Yükleme Başlatılıyor…') : retryLabel ?? tx('Dosyaları geri yükle')}
        </Button>
      </div>

      {overwrite && <TypedConfirmDialog
        open={confirmOpen}
        onClose={() => setConfirmOpen(false)}
        title={tx("Mevcut dosyaların üzerine yazılsın mı?")}
        consequence={{
          what: tx('{0} hedefindeki çakışan dosyalar yedekteki sürümleriyle değiştirilir.', { "0": targetReference }),
          who: tx('Bu dosyaları kullanan herkes ve uygulama etkilenir.'),
          reversible: tx('Hayır. Üzerine yazılan dosyaların eski sürümleri geri getirilemez.'),
        }}
        name={targetReference}
        pending={restoreStarting}
        error={failure}
        errorScope="restore"
        onConfirm={onStart}
        confirmLabel={retryLabel ?? tx("Üzerine yaz ve geri yükle")}
      />}
    </div>
  )
}
