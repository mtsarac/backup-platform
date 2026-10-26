import { tx, useLanguage } from '@/lib/i18n'
import { TechnicalDetails } from '@/components/ui/technical-details'
import { ArtifactEncryptionSection } from '@/features/artifact-encryption/artifact-encryption-section'
import { formatDateTime } from '@/lib/format'
import { ArtifactMetadata, ArtifactReplicas } from './artifact-details'
import { deletionInProgress, deletionReason } from './format'
import type { BackupArtifact } from './types'

/** Everything the page keeps out of sight: identifiers, checksums, exact times, retention internals, then the replica cards, encryption keys and engine metadata. */
export function ArtifactTechnical({ artifact }: { artifact: BackupArtifact }) {
  useLanguage()
  return <TechnicalDetails defaultOpen={deletionInProgress(artifact)} items={[
    { label: tx('Yedek dosyası kimliği'), value: artifact.id },
    { label: tx('Yedek dosyası sağlama toplamı'), value: artifact.artifactChecksum },
    { label: tx('Kaynak sağlama toplamı'), value: artifact.sourceChecksum },
    { label: tx('Sağlama toplamı algoritması'), value: artifact.checksumAlgorithm },
    { label: tx('Boyut (bayt)'), value: String(artifact.sizeBytes) },
    { label: tx('Oluşturulma'), value: formatDateTime(artifact.createdAt) },
    { label: tx('Kabul edilme'), value: formatDateTime(artifact.acceptedAt) },
    { label: tx('Saklama süresi sonu'), value: artifact.retentionExpiresAt ? formatDateTime(artifact.retentionExpiresAt) : tx('Süre sınırı yok') },
    { label: tx('Otomatik silme koruması'), value: artifact.retentionPinnedAt ? tx('Etkin · {0}', { '0': formatDateTime(artifact.retentionPinnedAt) }) : tx('Yok') },
    { label: tx('Fiziksel silme en erken'), value: formatDateTime(artifact.deleteAfter) },
    { label: tx('Silme nedeni'), value: deletionReason(artifact.deleteReason) },
  ]}>
    <ArtifactReplicas artifact={artifact} />
    <ArtifactEncryptionSection encryption={artifact.encryption} />
    <ArtifactMetadata artifact={artifact} />
  </TechnicalDetails>
}
