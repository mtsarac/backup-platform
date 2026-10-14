import type { SupportPermission, SupportScope, SupportSession, SupportStatus } from './types'

export const supportPermissions: readonly SupportPermission[] = ['ViewBackupMetadata', 'DownloadBackup', 'RestoreBackup']
export const supportStatuses: readonly SupportStatus[] = ['Active', 'Expired', 'Closed', 'Revoked']
export const permissionLabels: Record<SupportPermission, string> = { ViewBackupMetadata: 'Backup metadata görüntüle · ViewBackupMetadata', DownloadBackup: 'Backup indir · DownloadBackup', RestoreBackup: 'Yeni Database geri yükle · RestoreBackup' }
export const permissionShortLabels: Record<SupportPermission, string> = { ViewBackupMetadata: 'Metadata görüntüleme', DownloadBackup: 'Download', RestoreBackup: 'Yeni Database restore' }
export const permissionDescriptions: Record<SupportPermission, string> = {
  ViewBackupMetadata: 'Müşterinin Backup Job, Backup Run ve Artifact bilgilerini okur; Backup dosyasını indirme izni vermez.',
  DownloadBackup: 'Artifact dosyasını indirmeye izin verir.',
  RestoreBackup: 'Yalnızca yeni Database oluşturur; mevcut Database değiştirilmez.',
}
export const statusLabels: Record<SupportStatus, string> = { Active: 'Etkin', Expired: 'Süresi doldu', Closed: 'Kapatıldı', Revoked: 'Erişim geri alındı' }
export const endedMessages: Record<Exclude<SupportStatus, 'Active'>, string> = {
  Expired: 'Destek oturumunun süresi doldu. Müşteri verilerine erişim ve tüm işlemler kapalıdır.',
  Closed: 'Destek oturumu kapatıldı. Müşteri verilerine erişim ve tüm işlemler kapalıdır.',
  Revoked: 'Destek erişimi geri alındı. Müşteri verilerine erişim ve tüm işlemler kapalıdır.',
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
