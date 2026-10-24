import { tx, useLanguage } from '@/lib/i18n'
import { QueryState } from '@/components/data/query-state'
import { Button } from '@/components/ui/button'
import { Section } from '@/components/ui/section'
import { PrecheckResults } from './precheck-results'
import type { RestoreView } from './types'

interface RestoreCheckStepProps {
  /** Names the target server in the explanation; a file restore has none. */
  engineName?: string
  fileArchive?: boolean
  precheckData: RestoreView | undefined
  precheckLoading: boolean
  precheckDisplayError: unknown
  precheckError: unknown
  precheckRetryable: boolean
  precheckFailed: boolean
  precheckPassed: boolean
  precheckStarting: boolean
  restoreStarting: boolean
  onRunPrecheck: (fresh?: boolean) => void
  onBack: () => void
  onContinue: () => void
}

/** Shows the verdict of the check that matches the current settings; the way forward opens only once it has passed. */
export function RestoreCheckStep({ engineName, fileArchive, precheckData, precheckLoading, precheckDisplayError, precheckError, precheckRetryable, precheckFailed, precheckPassed, precheckStarting, restoreStarting, onRunPrecheck, onBack, onContinue }: RestoreCheckStepProps) {
  useLanguage()
  const busy = precheckStarting || restoreStarting
  return (
    <div className="space-y-5">
      <Section title={tx("Ön kontrol")} description={fileArchive
        ? tx("Neden soruluyor? Geri yüklemeye başlamadan önce hedefteki hiçbir dosyayı değiştirmeden her şeyin uygun olup olmadığını denetliyoruz.")
        : tx("Neden soruluyor? Geri yüklemeye başlamadan önce hedef {0} sunucusunda hiçbir şeyi değiştirmeden her şeyin uygun olup olmadığını denetliyoruz.", { "0": engineName })}>
        <QueryState isPending={precheckLoading && !precheckData} error={precheckDisplayError}>
          {precheckData && <PrecheckResults data={precheckData} failed={precheckFailed} passed={precheckPassed} fileArchive={fileArchive}
            onRecheck={() => onRunPrecheck(true)} recheckDisabled={busy} />}
        </QueryState>
        {(precheckError != null || (!precheckData && !precheckLoading)) && (
          <Button variant="outline" size="sm" onClick={() => onRunPrecheck()} disabled={busy}>
            {precheckRetryable ? tx('Tekrar Dene') : tx('Ön kontrolü çalıştır')}
          </Button>
        )}
      </Section>

      <div className="flex flex-wrap justify-between gap-3">
        <Button variant="outline" onClick={() => onBack()}>{tx("← Geri")}</Button>
        <Button disabled={!precheckPassed || busy} onClick={() => onContinue()}>{tx("Devam et →")}</Button>
      </div>
    </div>
  )
}
