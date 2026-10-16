import { tx, useLanguage } from '@/lib/i18n'
import { RotateCcw } from 'lucide-react'
import { Link } from 'react-router-dom'
import { Button } from '@/components/ui/button'
import { Notice } from '@/components/ui/notice'
import { Section } from '@/components/ui/section'
import { useTenant } from '@/features/tenancy/use-tenant'
import type { BackupArtifact } from '@/features/artifacts/types'
import { restoreIneligibility } from '@/features/artifacts/restore-eligibility'

export function ArtifactRestoreAction({ tenantId, artifact }: { tenantId: string; artifact: BackupArtifact }) {
  useLanguage()
  const { canOperate } = useTenant()

  if (!canOperate) {
    return <Notice tone="info" title={tx("Geri yükleme yetkiniz yok")}>{tx("Geri yükleme (Restore Backup) işlemi Operator, Admin veya Owner rolü gerektirir. Viewer rolü bu işlem için yetkili değildir.")}</Notice>
  }
  const ineligibilityReason = restoreIneligibility(artifact)
  return <Section title={tx("Geri yükleme (Restore)")} description={tx("Bu backup dosyasını aynı Database motorundaki hedef sunucuya yeni veritabanı olarak veya mevcut veritabanının üzerine geri yükleyin.")}
    actions={ineligibilityReason
      ? <Button disabled variant="outline">{tx("Restore Backup")}</Button>
      : <Link to={`/tenants/${tenantId}/artifacts/${artifact.id}/restore`}><Button className="inline-flex items-center gap-2"><RotateCcw aria-hidden="true" className="size-4" />{tx("Restore Backup")}</Button></Link>}>
    {ineligibilityReason
      ? <Notice tone="warning" title={tx("Bu backup şu anda geri yüklenemez")}>{ineligibilityReason}</Notice>
      : <p className="text-sm text-text-muted">{tx("Sihirbaz hedefi seçtirir, ön kontrol çalıştırır ve onayınızdan sonra başlatır.")}</p>}
  </Section>
}
