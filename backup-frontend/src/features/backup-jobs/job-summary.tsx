import { tx, useLanguage } from '@/lib/i18n'
import type { ReactNode } from 'react'
import { Button } from '@/components/ui/button'
import { Fact, Facts, Section } from '@/components/ui/section'
import { jobSteps, scheduleLabel } from '@/features/backup-jobs/job-wizard-model'
import type { BackupConfiguration, RetentionPolicy, RetryPolicy, Schedule } from '@/features/backup-jobs/types'
import { formatSeconds } from '@/features/backups/presentation'
import type { ConnectionView } from '@/features/storage-connections/types'

export interface JobSummaryProps {
  name: string; agentLabel: string; enabled: boolean; configuration: BackupConfiguration
  scheduled: boolean; schedule: Schedule; overlap: 'Skip' | 'Queue'; retry: RetryPolicy; retention: RetentionPolicy
  canAdmin: boolean; connections: readonly ConnectionView[]; nextRun?: string; onGo: (step: number) => void
}

const policyText = { get Disabled() { return tx("Kapalı") }, get Preferred() { return tx("Destekleniyorsa") }, get Required() { return tx("Zorunlu") } } as const

/** Read-only review of everything the wizard will save; each block links back to the step that owns it. */
export function JobSummary(props: JobSummaryProps) {
  useLanguage()
  const { configuration: c, schedule, retry, retention, onGo } = props
  const block = (step: number, children: ReactNode) => <Section title={tx(jobSteps[step])} actions={<Button type="button" variant="outline" size="sm" aria-label={tx("Adıma git: {0}", { "0": jobSteps[step] })} onClick={() => onGo(step)}>{tx("Adıma git")}</Button>}>{children}</Section>
  return <div className="space-y-4">
    {block(0, <Facts>
      <Fact label={tx("İş adı")}>{props.name || '—'}</Fact>
      <Fact label={tx("Agent")}>{props.agentLabel}</Fact>
      <Fact label={tx("Durum")}>{props.enabled ? tx('Etkin') : tx('Devre dışı')}</Fact>
    </Facts>)}
    {block(1, <Facts>
      <Fact label={c.engine === 'postgres' ? tx('PostgreSQL host') : tx('SQL Server host / instance')}>{c.host || '—'}</Fact>
      <Fact label={tx("Database")}>{c.database || '—'}</Fact>
      <Fact label={tx("Engine")}>{c.engine === 'postgres' ? tx('PostgreSQL · pg_dump custom Full') : 'Microsoft SQL Server'}</Fact>
      <Fact label={tx("Port")}>{c.port ?? tx('Varsayılan')}</Fact>
      <Fact label={tx("Kimlik doğrulama")}>{c.integratedSecurity ? tx('Windows kimlik doğrulaması') : (c.databaseCredentialReference ? tx("Kayıtlı veritabanı girişi") : tx("Veritabanı girişi girilmedi"))}</Fact>
      <Fact label={tx("Bağlantı")}>{c.encrypt ? tx('Şifreli') : tx('Şifresiz')}{c.trustServerCertificate ? tx(' · sunucu sertifikasına güvenilir') : ''}</Fact>
    </Facts>)}
    {block(2, <Facts>
      <Fact label={tx("Hedefler")}><ul className="space-y-1">{c.destinations.map((destination, index) => <li key={index}>{destination.storageProvider} · {destination.storageConnectionId ? props.connections.find(item => item.id === destination.storageConnectionId)?.name ?? tx('Bağlantı bulunamadı') : tx('Varsayılan bağlantı')} · {destination.storageExecutionLocation === 'Agent' ? tx('Agent diski') : 'Backend'}{destination.isPrimary ? tx(' · Birincil') : ''}</li>)}</ul></Fact>
      <Fact label={tx("Doğrulanmış Replica gereksinimi")}>{c.requiredVerifiedReplicaCount}</Fact>
      <Fact label={tx("Hedef öneki")}>{c.storagePrefix || tx('Yok')}</Fact>
      <Fact label={tx("Geçici alan (staging)")}>{c.stagingReference || '—'}</Fact>
    </Facts>)}
    {block(3, <Facts>
      <Fact label={tx("Zamanlama")}>{props.scheduled ? `${scheduleLabel(schedule)} · ${schedule.timeZone} · ${schedule.enabled ? 'etkin' : tx('kapalı')}` : 'Manuel'}</Fact>
      {props.scheduled && <Fact label={tx("Cron ifadesi")}><span className="font-mono">{schedule.cronExpression}</span></Fact>}
      {props.scheduled && <Fact label={tx("Kaçırılan çalışma toleransı")}>{formatSeconds(schedule.missedRunGraceSeconds)}</Fact>}
      {props.nextRun && <Fact label={tx("Sonraki çalışma")}>{props.nextRun}</Fact>}
      <Fact label={tx("Önceki çalışma sürerken")}>{props.overlap === 'Skip' ? tx('Atla (Skip)') : tx('Kuyruğa al (Queue)')}</Fact>
      <Fact label={tx("Yeniden deneme")}>{retry.maxAttempts}{' '}{tx("deneme ·")}{' '}{retry.backoff} · {formatSeconds(retry.initialDelaySeconds)} – {formatSeconds(retry.maxDelaySeconds)}{' '}{tx("· jitter")}{' '}{retry.jitter}</Fact>
    </Facts>)}
    {block(4, <Facts>
      <Fact label={tx("Saklama")}>{!props.canAdmin && props.nextRun === undefined ? tx('Sunucunun varsayılan politikası uygulanır.') : retention.enabled ? tx("{0} · silme bekleme {1}", { "0": formatSeconds(retention.keepForSeconds), "1": formatSeconds(retention.deleteGracePeriodSeconds) }) : tx('Süresiz')}{!props.canAdmin && <span className="block text-sm text-text-muted">{tx("Yalnızca Owner / Admin değiştirebilir.")}</span>}</Fact>
      <Fact label={tx("Kopya yedek (COPY_ONLY)")}>{c.engine === 'postgres' ? tx('Mantıksal arşiv yedek zincirini değiştirmez') : c.copyOnly ? tx('Açık') : tx('Kapalı')}</Fact>
      <Fact label={c.engine === 'postgres' ? tx('Sayfa bütünlüğü (data_checksums)') : tx('Bütünlük doğrulaması (CHECKSUM)')}>{policyText[c.checksumPolicy]}</Fact>
      <Fact label={tx("Sıkıştırma (Compression)")}>{policyText[c.compressionPolicy]}</Fact>
    </Facts>)}
  </div>
}
