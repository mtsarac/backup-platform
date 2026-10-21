# 02: Kapasite Eşikleri ve Kritik Doluluk Bildirimleri (N1 Integration)

**What to build:** Depolama doluluk oranının platform genel eşikleriyle (%80 Warning, %90 Critical) değerlendirilmesi, görsel uyarı durumlarının üretilmesi ve %90 Critical eşiği aşıldığında N1 bildirim outbox'ına tekil `storage_capacity_critical` olayının düşürülmesi. Başarılı/başarısız her yedekleme transferi sonrasında otomatik anlık probe tetiklenmesi ve 24 saatte bir periyodik tarama arka plan servisinin çalışması.

**Blocked by:** 01: Hibrit Depolama Kapasitesi Sorgulama (CapacityProbe & API)

**Status:** done

- [x] Sabit platform eşikleri (%80 Warning, %90 Critical) üzerinden `StorageCapacityStatus` değerlendirmesi yapıldı.
- [x] Bir depolama hedefi %90 doluluğa ulaştığında `storage_capacity_critical` olay tipi snapshot ile aynı veritabanı transaction'ında N1 notification outbox tablosuna atomik olarak yazılır.
- [x] Bildirim motoru yinelenen kritik durumlar için deduplication ve suppression kurallarını uygulayarak alıcıyı spam'e boğmaz.
- [x] Yedekleme orkestratörü (`BackupOrchestrator`) her transfer tamamlandığında hedef bağlantı için asenkron `CapacityProbe` tetikler.
- [x] Arka planda çalışan periyodik HostedService (günde bir) son 24 saatte güncellenmemiş depolama bağlantılarını otomatik olarak probe eder.
- [x] Depolama listesinde ve detay sayfasında %80 üzeri için sarı `Warning`, %90 üzeri için kırmızı `Critical` rozetleri görüntülenir.
- [x] Integration testleri: %80 ve %90 eşik geçişleri, N1 event kaydı, deduplication ve transfer sonrası otomatik tetiklenme senaryoları test edildi ve geçti.

## Comments

- Yeniden açıldı: `StorageCapacityProbeService.SaveAndNotifyAsync` önce snapshot'ı kaydedip daha sonra bildirim sweep'i çağırıyor ve bildirim hatasını yakalıyor. Snapshot/outbox atomikliği ve gerçek Agent transfer-sonrası probe yolu 01'in üretim taşımasıyla birlikte doğrulanacak.

## Verification

- Snapshot kaydı, audit olayı ve `storage_capacity_critical` N1 outbox bildirimi tek bir PostgreSQL transaction'ında commit edilir.
- `StorageCapacityProbeIntegrationTests` ve `StorageCapacityThresholdsAndAlertsIntegrationTests` altında veritabanı rollback senaryolarında tüm parçaların birlikte geri alındığı doğrulandı.
