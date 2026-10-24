import type { ReactNode } from 'react'
import { tx, useLanguage } from '@/lib/i18n'
import { StatusPill } from '@/components/data/status-pill'
import { Section } from '@/components/ui/section'
import type { StatusLevel } from '@/lib/status'
import { formatDateTime, formatRelativeTime } from '@/lib/format'
import { artifactState, deletionInProgress } from './format'
import { restoreIneligibility } from './restore-eligibility'
import type { BackupArtifact } from './types'

interface Answer { level: StatusLevel; label: string; text: ReactNode }

function intact(artifact: BackupArtifact): Answer {
  if (artifact.state === 'Deleted') return { level: 'neutral', label: tx('Silindi'), text: tx('Fiziksel dosyalar silindi; yalnızca geçmiş kaydı kaldı.') }
  const verified = artifact.replicas.filter(replica => replica.verificationState === 'Verified' && replica.usable).length
  if (verified > 0) return { level: 'safe', label: tx('Sağlam'), text: tx('{0} kopyanın bütünlüğü doğrulandı.', { '0': verified }) }
  return { level: 'attention', label: tx('Doğrulanmadı'), text: tx('Bütünlüğü doğrulanmış bir kopya yok. Bu dosyaya güvenmeyin; yeni bir yedek almayı düşünün.') }
}

function restorable(artifact: BackupArtifact, canOperate: boolean): Answer {
  const reason = restoreIneligibility(artifact)
  if (reason) return { level: artifact.state === 'Deleted' ? 'neutral' : 'attention', label: tx('Geri yüklenemez'), text: reason }
  return { level: 'safe', label: tx('Geri yüklenebilir'), text: canOperate ? tx('Sihirbaz hedefi seçtirir, ön kontrol yapar ve onayınızdan sonra başlatır.') : tx('Geri yüklemeyi başlatma yetkiniz yok. Yöneticinize danışın.') }
}

function deletion(artifact: BackupArtifact, now: number): Answer {
  const state = artifactState(artifact)
  if (state === 'Deleted') return { level: 'neutral', label: tx('Silindi'), text: tx('Fiziksel dosyalar silindi.') }
  if (state === 'DeleteFailed') return { level: 'urgent', label: tx('Silme başarısız'), text: tx('Silme tamamlanamadı; dosyalar depoda duruyor. Diğer kopyalar etkilenmedi.') }
  if (deletionInProgress(artifact) || artifact.state === 'DeletePending') {
    const deleteAfter = artifact.deleteAfter ? Date.parse(artifact.deleteAfter) : NaN
    const text = Number.isNaN(deleteAfter) ? tx('Silme isteği alındı.')
      : deleteAfter <= now ? tx('Bekleme süresi doldu; fiziksel silme başlamak üzere.')
        : <span title={formatDateTime(artifact.deleteAfter)}>{tx('Dosyalar {0} silinir.', { '0': formatRelativeTime(artifact.deleteAfter, new Date(now)) })}</span>
    return { level: 'attention', label: tx('Silme bekliyor'), text }
  }
  if (artifact.retentionPinnedAt) return { level: 'safe', label: tx('Korumalı'), text: tx('Otomatik silme koruması etkin; saklama süresi dolsa bile şimdilik silinmez.') }
  if (artifact.retentionExpiresAt) {
    return Date.parse(artifact.retentionExpiresAt) <= now
      ? { level: 'attention', label: tx('Süresi doldu'), text: tx('Saklama süresi doldu; silme işlemi yakında başlar.') }
      : { level: 'safe', label: tx('Saklanıyor'), text: tx('Saklama süresi dolunca silinir: {0}.', { '0': formatDateTime(artifact.retentionExpiresAt) }) }
  }
  return { level: 'neutral', label: tx('Süre sınırı yok'), text: tx('Siz silme isteği oluşturmadıkça saklanır.') }
}

/** The three things a reader came for, before any data: is the file intact, can it be restored, when will it be deleted. */
export function ArtifactAnswers({ artifact, canOperate, now }: { artifact: BackupArtifact; canOperate: boolean; now: number }) {
  useLanguage()
  const answers: [string, Answer][] = [
    [tx('Dosya sağlam mı?'), intact(artifact)],
    [tx('Geri yüklenebilir mi?'), restorable(artifact, canOperate)],
    [tx('Ne zaman silinecek?'), deletion(artifact, now)],
  ]
  return <Section title={tx('Kısaca durum')}>
    <dl className="grid gap-5 md:grid-cols-3">
      {answers.map(([question, { level, label, text }]) => <div key={question} className="min-w-0 space-y-1.5">
        <dt className="text-sm text-text-muted">{question}</dt>
        <dd className="space-y-1.5"><StatusPill level={level}>{label}</StatusPill><p className="text-sm text-text">{text}</p></dd>
      </div>)}
    </dl>
  </Section>
}
