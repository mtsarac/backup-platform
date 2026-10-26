import { tx, useLanguage } from '@/lib/i18n'
import type { ReactNode } from 'react'
import { Button } from '@/components/ui/button'
import { Question } from '@/features/backup-jobs/job-form-fields'
import { TechnicalDetails, type TechnicalItem } from '@/components/ui/technical-details'
import { agentStep, jobSteps, keepForLabel, nameStep, protectionStep, retentionStep, scheduleStep, schedulePresetLabel, sourceStep, storageStep } from '@/features/backup-jobs/job-wizard-model'
import type { BackupConfiguration, RetentionPolicy, RetryPolicy, Schedule } from '@/features/backup-jobs/types'
import { policyLabels } from '@/features/artifact-encryption/labels'
import type { ConnectionView } from '@/features/storage-connections/types'
import { formatSeconds } from '@/lib/format'

export interface JobSummaryProps {
  name: string; agentLabel: string; enabled: boolean; configuration: BackupConfiguration
  scheduled: boolean; schedule: Schedule; overlap: 'Skip' | 'Queue'; retry: RetryPolicy; retention: RetentionPolicy
  canAdmin: boolean; connections: readonly ConnectionView[]; nextRun?: string; existing: boolean; onGo: (step: number) => void
}

const policyText = { get Disabled() { return tx("Kapalı") }, get Preferred() { return tx("Destekleniyorsa") }, get Required() { return tx("Zorunlu") } } as const

/** Plain-language review of everything the wizard will save; technical values sit in the collapsed details. */
export function JobSummary(props: JobSummaryProps) {
  useLanguage()
  const { configuration: c, schedule, retry, retention, onGo } = props
  const isFiles = c.engine === 'file-system'
  const row = (step: number, children: ReactNode) => <div key={step} className="flex flex-wrap items-start justify-between gap-3 border-b border-border py-3 last:border-b-0">
    <div className="min-w-0"><dt className="text-sm text-text-muted">{tx(jobSteps[step])}</dt><dd className="mt-0.5 break-words text-[0.9375rem] text-text">{children}</dd></div>
    <Button type="button" variant="outline" size="sm" aria-label={tx("Değiştir: {0}", { "0": tx(jobSteps[step]) })} onClick={() => onGo(step)}>{tx("Değiştir")}</Button>
  </div>
  const retentionText = !props.canAdmin && !props.existing ? tx('Sunucunun varsayılan politikası uygulanır.') : retention.enabled ? keepForLabel(retention.keepForSeconds) : tx('Süresiz')
  const technical: TechnicalItem[] = [
    ...(isFiles
      ? [{ label: tx('Dahil Edilecekler'), value: c.files?.includes.join('\n') || '**' }, { label: tx('Hariç Tutulacaklar'), value: c.files?.excludes.join('\n') || tx('Yok') }, { label: tx('Sembolik Link Politikası'), value: c.files?.symlinkPolicy === 'Preserve' ? tx('Koru') : tx('Atla') }]
      : [{ label: tx("Port"), value: c.port === null ? tx('Varsayılan') : String(c.port) },
        { label: tx("Kimlik doğrulama"), value: c.integratedSecurity ? tx('Windows kimlik doğrulaması') : c.databaseCredentialReference ? tx("Kayıtlı veritabanı girişi") : tx("Veritabanı girişi girilmedi") },
        { label: tx("Bağlantı"), value: [c.encrypt ? tx('Şifreli') : tx('Şifresiz'), c.trustServerCertificate && tx('sunucu sertifikasına güvenilir')].filter(Boolean).join(' · ') }]),
    ...c.destinations.map((destination, index) => ({ label: tx("Hedef {0}", { "0": index + 1 }), value: [destination.storageProvider, destination.storageExecutionLocation === 'Agent' ? tx('Agent diski') : 'Backend', destination.isPrimary && tx('Birincil')].filter(Boolean).join(' · ') })),
    { label: tx("Doğrulanmış Replica gereksinimi"), value: String(c.requiredVerifiedReplicaCount) },
    ...(c.enforceThreeTwoOne ? [{ label: tx('3-2-1 koruması'), value: `${tx('3 kopya · 2 medya · doğrulanmış off-site')} · ${c.sourceSite}` }] : []),
    { label: tx("Hedef öneki"), value: c.storagePrefix || tx('Yok') },
    { label: tx("Geçici alan (staging)"), value: c.stagingReference || '—' },
    ...(props.scheduled ? [
      { label: tx("Cron ifadesi"), value: schedule.cronExpression },
      { label: tx("Saat dilimi"), value: schedule.timeZone },
      { label: tx("Zamanlama etkin"), value: schedule.enabled ? tx('Evet') : tx('Hayır') },
      { label: tx("Kaçırılan çalışma toleransı"), value: formatSeconds(schedule.missedRunGraceSeconds) }] : []),
    { label: tx("Önceki çalışma sürerken"), value: props.overlap === 'Skip' ? tx('Atla (Skip)') : tx('Kuyruğa al (Queue)') },
    { label: tx("Yeniden deneme"), value: tx("{0} deneme · {1} · {2} – {3} · jitter {4}", { "0": retry.maxAttempts, "1": retry.backoff, "2": formatSeconds(retry.initialDelaySeconds), "3": formatSeconds(retry.maxDelaySeconds), "4": retry.jitter }) },
    { label: tx("Saklama süresi (saniye)"), value: String(retention.keepForSeconds) },
    { label: tx("Silme bekleme süresi (saniye)"), value: String(retention.deleteGracePeriodSeconds) },
    ...(isFiles ? [] : [
      ...(c.engine === 'mssql' ? [{ label: tx("Kopya yedek (COPY_ONLY)"), value: c.copyOnly ? tx('Açık') : tx('Kapalı') }] : []),
      { label: tx("Bütünlük doğrulaması (CHECKSUM)"), value: policyText[c.checksumPolicy] }]),
    { label: tx("Sıkıştırma (Compression)"), value: policyText[c.compressionPolicy] },
  ]
  return <div className="space-y-4">
    <Question title={tx("Her şey doğru mu?")} why={tx("Kaydet'e basmadan önce neyin oluşturulacağını son kez görürsünüz; hiçbir şey o ana kadar değişmez.")}>
      <dl>
        {row(nameStep, props.enabled ? props.name || '—' : tx("{0} (devre dışı)", { "0": props.name || '—' }))}
        {row(agentStep, props.agentLabel)}
        {row(sourceStep, isFiles
          ? <>{tx('Dosya / Klasör')} · {c.database}<span className="block whitespace-pre-line">{c.files?.paths.join('\n')}</span></>
          : `${c.engine === 'mssql' ? 'Microsoft SQL Server' : 'PostgreSQL'} · ${c.database || '—'} (${c.host || '—'})`)}
        {row(storageStep, <ul className="space-y-1">{c.destinations.map((destination, index) => <li key={index}>{destination.storageConnectionId ? props.connections.find(item => item.id === destination.storageConnectionId)?.name ?? tx('Bağlantı bulunamadı') : tx('Varsayılan bağlantı')}</li>)}</ul>)}
        {row(scheduleStep, <>{props.scheduled ? schedulePresetLabel(schedule.cronExpression) ?? tx('Özel zamanlama') : tx('Elle çalıştırılır (zamanlama yok)')}{props.nextRun && <span className="block text-sm text-text-muted">{tx("Sonraki çalışma: {0}", { "0": props.nextRun })}</span>}</>)}
        {row(retentionStep, <>{retentionText}{!props.canAdmin && <span className="block text-sm text-text-muted">{tx("Yalnızca Owner / Admin değiştirebilir.")}</span>}</>)}
        {row(protectionStep, policyLabels[c.encryptionPolicy])}
      </dl>
    </Question>
    <TechnicalDetails items={technical} />
  </div>
}
