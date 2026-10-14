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

const policyText = { Disabled: 'Kapalı', Preferred: 'Destekleniyorsa', Required: 'Zorunlu' } as const

/** Read-only review of everything the wizard will save; each block links back to the step that owns it. */
export function JobSummary(props: JobSummaryProps) {
  const { configuration: c, schedule, retry, retention, onGo } = props
  const block = (step: number, children: ReactNode) => <Section title={jobSteps[step]} actions={<Button type="button" variant="outline" size="sm" aria-label={`Adıma git: ${jobSteps[step]}`} onClick={() => onGo(step)}>Adıma git</Button>}>{children}</Section>
  return <div className="space-y-4">
    {block(0, <Facts>
      <Fact label="İş adı">{props.name || '—'}</Fact>
      <Fact label="Agent">{props.agentLabel}</Fact>
      <Fact label="Durum">{props.enabled ? 'Etkin' : 'Devre dışı'}</Fact>
    </Facts>)}
    {block(1, <Facts>
      <Fact label="SQL Server host / instance">{c.host || '—'}</Fact>
      <Fact label="Database">{c.database || '—'}</Fact>
      <Fact label="Port">{c.port ?? 'Varsayılan'}</Fact>
      <Fact label="Kimlik doğrulama">{c.integratedSecurity ? 'Windows kimlik doğrulaması' : `Kimlik bilgisi referansı: ${c.databaseCredentialReference ?? '—'}`}</Fact>
      <Fact label="Bağlantı">{c.encrypt ? 'Şifreli' : 'Şifresiz'}{c.trustServerCertificate ? ' · sunucu sertifikasına güvenilir' : ''}</Fact>
    </Facts>)}
    {block(2, <Facts>
      <Fact label="Hedefler"><ul className="space-y-1">{c.destinations.map((destination, index) => <li key={index}>{destination.storageProvider} · {destination.storageConnectionId ? props.connections.find(item => item.id === destination.storageConnectionId)?.name ?? 'Bağlantı bulunamadı' : 'Varsayılan bağlantı'} · {destination.storageExecutionLocation === 'Agent' ? 'Agent diski' : 'Backend'}{destination.isPrimary ? ' · Birincil' : ''}</li>)}</ul></Fact>
      <Fact label="Doğrulanmış Replica gereksinimi">{c.requiredVerifiedReplicaCount}</Fact>
      <Fact label="Hedef öneki">{c.storagePrefix || 'Yok'}</Fact>
      <Fact label="Geçici alan (staging)">{c.stagingReference || '—'}</Fact>
    </Facts>)}
    {block(3, <Facts>
      <Fact label="Zamanlama">{props.scheduled ? `${scheduleLabel(schedule)} · ${schedule.timeZone} · ${schedule.enabled ? 'etkin' : 'kapalı'}` : 'Manuel'}</Fact>
      {props.scheduled && <Fact label="Cron ifadesi"><span className="font-mono">{schedule.cronExpression}</span></Fact>}
      {props.scheduled && <Fact label="Kaçırılan çalışma toleransı">{formatSeconds(schedule.missedRunGraceSeconds)}</Fact>}
      {props.nextRun && <Fact label="Sonraki çalışma">{props.nextRun}</Fact>}
      <Fact label="Önceki çalışma sürerken">{props.overlap === 'Skip' ? 'Atla (Skip)' : 'Kuyruğa al (Queue)'}</Fact>
      <Fact label="Yeniden deneme">{retry.maxAttempts} deneme · {retry.backoff} · {formatSeconds(retry.initialDelaySeconds)} – {formatSeconds(retry.maxDelaySeconds)} · jitter {retry.jitter}</Fact>
    </Facts>)}
    {block(4, <Facts>
      <Fact label="Saklama">{!props.canAdmin && props.nextRun === undefined ? 'Sunucunun varsayılan politikası uygulanır.' : retention.enabled ? `${formatSeconds(retention.keepForSeconds)} · silme bekleme ${formatSeconds(retention.deleteGracePeriodSeconds)}` : 'Süresiz'}{!props.canAdmin && <span className="block text-sm text-text-muted">Yalnızca Owner / Admin değiştirebilir.</span>}</Fact>
      <Fact label="Kopya yedek (COPY_ONLY)">{c.copyOnly ? 'Açık' : 'Kapalı'}</Fact>
      <Fact label="Bütünlük doğrulaması (CHECKSUM)">{policyText[c.checksumPolicy]}</Fact>
      <Fact label="Sıkıştırma (Compression)">{policyText[c.compressionPolicy]}</Fact>
    </Facts>)}
  </div>
}
