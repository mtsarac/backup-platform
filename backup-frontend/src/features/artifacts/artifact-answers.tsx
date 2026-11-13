import type { ReactNode } from 'react'
import { tx, useLanguage } from '@/lib/i18n'
import { StatusPill } from '@/components/data/status-pill'
import { Section, Fact, Facts } from '@/components/ui/section'
import type { StatusLevel } from '@/lib/status'
import { formatDateTime } from '@/lib/format'
import { deletionInProgress } from './format'
import { artifactAvailability } from './availability'
import type { BackupArtifact } from './types'

interface Answer { level: StatusLevel; label: string; text: ReactNode }

function intact(artifact: BackupArtifact): Answer {
  const verified = artifact.replicas.filter(replica => replica.verificationState === 'Verified' && replica.usable).length
  if (verified > 0) return { level: 'safe', label: tx('Sağlam'), text: artifact.replication ? null : tx('{0} kopyanın bütünlüğü doğrulandı.', { '0': verified }) }
  return { level: 'attention', label: tx('Doğrulanmadı'), text: tx('Bütünlüğü doğrulanmış bir kopya yok. Bu dosyaya güvenmeyin; yeni bir yedek almayı düşünün.') }
}

function restorable(artifact: BackupArtifact, canOperate: boolean): Answer {
  const { level, label, reason } = artifactAvailability(artifact)
  if (level !== 'safe') return { level, label, text: reason }
  return { level, label, text: canOperate ? tx('Sihirbaz hedefi seçtirir, ön kontrol yapar ve onayınızdan sonra başlatır.') : tx('Geri yüklemeyi başlatma yetkiniz yok. Yöneticinize danışın.') }
}

function deletion(artifact: BackupArtifact, now: number): Answer {
  if (artifact.retentionPinnedAt) return { level: 'safe', label: tx('Korumalı'), text: tx('Otomatik silme koruması etkin; saklama süresi dolsa bile şimdilik silinmez.') }
  if (artifact.retentionExpiresAt) {
    return Date.parse(artifact.retentionExpiresAt) <= now
      ? { level: 'attention', label: tx('Süresi doldu'), text: tx('Saklama süresi doldu; silme işlemi yakında başlar.') }
      : { level: 'safe', label: tx('Saklanıyor'), text: tx('Saklama süresi dolunca silinir: {0}.', { '0': formatDateTime(artifact.retentionExpiresAt) }) }
  }
  return { level: 'neutral', label: tx('Süre sınırı yok'), text: tx('Siz silme isteği oluşturmadıkça saklanır.') }
}

/** The three things a reader came for, before any data: integrity, restore availability and retention. */
export function ArtifactAnswers({ artifact, canOperate, now }: { artifact: BackupArtifact; canOperate: boolean; now: number }) {
  useLanguage()
  if (artifact.state === 'Deleted') return null
  const answers: [string, Answer][] = [
    [tx('Bütünlük'), intact(artifact)],
    [tx('Geri yükleme'), restorable(artifact, canOperate)],
  ]
  if (!deletionInProgress(artifact) && artifact.state !== 'DeletePending') answers.push([tx('Saklama ve silme'), deletion(artifact, now)])
  return <Section title={tx('Kısaca durum')}>
    <Facts columns={3}>
      {answers.map(([question, { level, label, text }]) => <Fact key={question} label={question}><span className="block space-y-1.5"><StatusPill level={level}>{label}</StatusPill>{text && <span className="block text-sm text-text">{text}</span>}</span></Fact>)}
    </Facts>
  </Section>
}
