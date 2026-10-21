# 05: Gözlemlenebilirlik Dashboard Özeti ve Uçtan Uca Kapanış

**What to build:** Tenant genel bakış ekranında (Dashboard) depolama kapasite risklerini, kritik/düşük sağlık skorlu işleri ve son boyut anomalisi uyarılarını tek bir birleşik gözlemlenebilirlik merkezinde toplayan arayüz ve uçtan uca kabul kapanışı. Depolama doluluk aşımı, sağlık puanı düşüşü, boyut anomalisi tespiti ve N1 bildirim gönderimini baştan sona doğrulayan E2E entegrasyon test takımı.

**Blocked by:** 02: Kapasite Eşikleri ve Kritik Doluluk Bildirimleri (N1 Integration), 03: İstatistiksel Boyut Anomalisi Tespiti (Z-Score & SizeAnomalyWarning), 04: Kompozit Backup Health Durumu ve Puanlama Motoru

**Status:** done

- [x] Ana Dashboard üzerinde "Gözlemlenebilirlik & Güvenilirlik" paneli:
  - Tenant genel sağlık skoru ortalaması ve `Healthy / Degraded / Critical` iş sayaçları.
  - %80 ve %90 üzeri doluluğa sahip depolama hedefleri listesi ve aciliyet rozetleri.
  - Son 7 günde tespit edilen `SizeAnomalyWarning` olaylarının hızlı özeti ve ilgili işe yönlendirme.
- [x] Uçtan uca E2E senaryo testi:
  - 1. Hedef depolamanın %92 doluluk bildirmesi -> `storage_capacity_critical` N1 bildirimi.
  - 2. İlgili hedefe bağlı işin sağlık puanının kapasite bileşeni nedeniyle düşmesi.
  - 3. İşin yeni bir yedeği tamamlandığında normalden %40 küçük artifact üretmesi -> `backup_size_anomaly` N1 bildirimi + `SizeAnomalyWarning` advisory rozeti.
  - 4. Yedeğin fail olmadan başarıyla tamamlandığının ve tüm metriklerin dashboard API'sinde doğru yansıdığının doğrulanması.
- [x] Önceden alınmış mevcut yedeklerin, replikaların ve eski Agent sürümlerinin veri bütünlüğünün ve geriye uyumluluğunun korunması.
- [x] `docs/plan/Proje_Durumu.md` üzerinde 5A.1 gözlemlenebilirlik çekirdeği tamamlandı durumunun ve test kanıtlarının güncellenmesi.

## Verification

- Dashboard gözlemlenebilirlik paneli eklendi ve vitest testleri ile doğrulandı.
- Uçtan uca entegrasyon senaryosu `BackupSizeAnomalyIntegrationTests.Capacity_and_size_warnings_flow_through_agent_http_to_health_summary_and_real_notifications` altında çalıştırıldı ve geçti.
- Canlı Chromium üzerinde gerçek API oturumunda Dashboard ve mobil görünüm doğrulandı.
