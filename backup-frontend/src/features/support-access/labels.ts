import { tx } from '@/lib/i18n'

export const supportActionLabels: Record<string, string> = {
  get 'support.session.opened'() { return tx('Destek erişimi başlatıldı') },
  get 'support.session.closed'() { return tx('Destek erişimi kapatıldı') },
  get 'support.session.expired'() { return tx('Destek erişiminin süresi doldu') },
  get 'support.session.revoked'() { return tx('Destek erişimi geri alındı') },
  get 'support.artifact.viewed'() { return tx('Yedek dosyası görüntülendi') },
  get 'support.artifact.listed'() { return tx('Yedek dosyaları listelendi') },
  get 'support.backup.viewed'() { return tx('Yedekleme bilgileri görüntülendi') },
  get 'support.artifact.download_requested'() { return tx('Yedek indirme istendi') },
  get 'support.artifact.download_started'() { return tx('Yedek indirme başladı') },
  get 'support.artifact.download_completed'() { return tx('Yedek indirme tamamlandı') },
  get 'support.artifact.download_failed'() { return tx('Yedek indirilemedi') },
  get 'support.restore.precheck_requested'() { return tx('Geri yükleme ön kontrolü istendi') },
  get 'support.restore.requested'() { return tx('Geri yükleme istendi') },
  get 'support.restore.cancel_requested'() { return tx('Geri yüklemenin iptali istendi') },
  get 'support.restore.completed'() { return tx('Geri yükleme tamamlandı') },
  get 'support.restore.failed'() { return tx('Geri yükleme başarısız oldu') },
  get 'support.access_denied'() { return tx('Destek erişimine izin verilmedi') },
}
