# 01: Hibrit Depolama Kapasitesi Sorgulama (CapacityProbe & API)

**What to build:** Depolama bağlantılarının fiziksel disk ve kota doluluk durumlarının hibrit yolla sorgulanması. Bulut sağlayıcılar (Google Drive) doğrudan Backend tarafından sorgulanırken; yerel ve özel ağ depolamaları (Local disk, LAN SFTP) yetkili bir Agent'a delege edilen `CapacityProbe` görevi ile sorgulanır. Sonuçlar `StorageCapacitySnapshot` tablosuna kaydedilir, REST API ile sunulur ve Depolama Detay arayüzünde kalan/kullanılan alan görselleştirilir.

**Blocked by:** None (can start immediately)

**Status:** done

- [x] `StorageCapacitySnapshot` veritabanı tablosu ve EF Core entity modeli (`ConnectionId`, `TotalBytes`, `UsedBytes`, `FreeBytes`, `ReportedAt`) oluşturuldu ve migration uygulandı.
- [x] `StorageCapacityProbeService` hibrit yönlendirmeyi uygular: Google Drive ve Central bağlantılarında backend doğrudan kota ve dosya sistemi alanlarını sorgular; Local ve SFTP bağlantılarında ise aktif yetkili Agent'a kalıcı, lease/fencing korumalı poll görevi delege eder.
- [x] Agent tarafında `CapacityProbeTaskRequest` işleyicisi gerçek task dispatcher (`CapacityProbeTaskWorker`) üzerinden çalışır: Yerel disk için yapılandırılmış root veya volume üzerinden `DriveInfo`, SFTP için güvenli SSH/SFTP oturumu ve `statvfs` ile `CapacityProbeTaskResult` döner.
- [x] Hedefe erişebilen aktif bir Agent bulunamazsa durum sessizce sıfır veya sahte veri basmak yerine `CapacityUnavailable` olarak raporlanır.
- [x] `GET /api/v1/storage/connections/{id}/capacity` ve `POST /api/v1/storage/connections/{id}/probe` REST API endpoint'leri çalışır ve yetkilendirme (tenant isolation) doğrulanır.
- [x] Storage Hub / Depolama Detay arayüzünde toplam, kullanılan ve boş alan metrikleri ilerleme çubuğu ile gösterilir; manuel "Kapasiteyi Sorgula" butonu API tetikler.
- [x] Integration testleri: Backend doğrudan Drive ve Central sorguları, gerçek yayınlanmış `BackupPlatform.Agent.Host` executable'ı ile yerel disk ve izole gerçek SFTP container'ı (`atmoz/sftp`) üzerinden test edildi ve geçti.

## Comments

- Yeniden açıldı: Üretim `DefaultAgentCapacityProbeDispatcher` hiçbir taşıma delegate'i olmadan yalnız `CapacityUnavailable` döndürüyor; Agent handler kayıtlı olsa da task poll/dispatch yoluna bağlı değil. Üretim taşıması ve gerçek Agent yürütme kanıtı tamamlanmadan 01/02 doğrulanmış sayılmayacak.

## Verification

- Üretim taşıması `78190112` ve `386b60d` ile tamamlandı: `capacity-probe` task tipi ve `storage.capacity-probe` ajan yeteneği ile kalıcı poll/lease kuyruğu kuruldu; sahte delegate kaldırıldı.
- `BackupPlatform.Agent.Host` yayınlanarak `StorageCapacityAgentHostSmokeTests` altında hem yerel disk hem de gerçek SFTP sunucusu (`atmoz/sftp`, statvfs, operatör ağ izin listesi `127.0.0.1/32`) üzerinde yürütüldü ve başarıyla geçti.
