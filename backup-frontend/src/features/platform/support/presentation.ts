import { tx } from '@/lib/i18n'
import type { SupportPermission, SupportScope, SupportSession, SupportStatus } from './types'

export const supportPermissions: readonly SupportPermission[] = ['ViewBackupMetadata', 'DownloadBackup', 'RestoreBackup']
export const supportStatuses: readonly SupportStatus[] = ['Active', 'Expired', 'Closed', 'Revoked']
export const permissionLabels: Record<SupportPermission, string> = {
  get ViewBackupMetadata() { return `${tx('Backup metadata görüntüle')} · ViewBackupMetadata` },
  get DownloadBackup() { return `${tx('Backup indir')} · DownloadBackup` },
  get RestoreBackup() { return `${tx('Yeni Database geri yükle')} · RestoreBackup` },
}
export const permissionShortLabels: Record<SupportPermission, string> = {
  get ViewBackupMetadata() { return tx("Metadata görüntüleme") },
  get DownloadBackup() { return tx("Download") },
  get RestoreBackup() { return tx("Yeni Database restore") },
}
export const permissionDescriptions: Record<SupportPermission, string> = {
  get ViewBackupMetadata() { return tx("Müşterinin Backup Job, Backup Run ve Artifact bilgilerini okur; Backup dosyasını indirme izni vermez.") },
  get DownloadBackup() { return tx("Artifact dosyasını indirmeye izin verir.") },
  get RestoreBackup() { return tx("Yalnızca yeni Database oluşturur; mevcut Database değiştirilmez.") },
}
export const endedMessages: Record<Exclude<SupportStatus, 'Active'>, string> = {
  get Expired() { return tx("Destek oturumunun süresi doldu. Müşteri verilerine erişim ve tüm işlemler kapalıdır.") },
  get Closed() { return tx("Destek oturumu kapatıldı. Müşteri verilerine erişim ve tüm işlemler kapalıdır.") },
  get Revoked() { return tx("Destek erişimi geri alındı. Müşteri verilerine erişim ve tüm işlemler kapalıdır.") },
}
export interface SupportAccess { view: boolean; download: boolean; restore: boolean; downloads: boolean; restores: boolean }
export function supportAccess(session: SupportSession): SupportAccess {
  const usable = session.status === 'Active' && session.isOwner
  const view = usable && session.permissions.includes('ViewBackupMetadata')
  const download = usable && session.permissions.includes('DownloadBackup')
  const restore = usable && session.permissions.includes('RestoreBackup')
  return { view, download, restore, downloads: view || download, restores: view || restore }
}
export function supportScope(session: SupportSession, permission: keyof SupportAccess): SupportScope {
  return { sessionId: session.id, tenantId: session.tenantId, enabled: supportAccess(session)[permission] }
}
