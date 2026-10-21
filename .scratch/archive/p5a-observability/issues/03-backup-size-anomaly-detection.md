# 03: İstatistiksel Boyut Anomalisi Tespiti (Z-Score & SizeAnomalyWarning)

**What to build:** Başarılı yedekleme attempt'leri sonrasında artifact boyutunun geçmişe göre istatistiksel trendden belirgin şekilde sapıp sapmadığının tespit edilmesi. Son 10 başarılı yedeğin hareketli ortalaması ve Z-Score analizi ile >%30 veya >2σ sapmalar çift yönlü (ani düşüş veya devasa şişme) tespit edilir. Bu durum yedeği kesinlikle fail etmez (advisory warning); run detayına `SizeAnomalyWarning` ekler ve N1 outbox'ına `backup_size_anomaly` bildirimini yazar.

**Blocked by:** None (can start immediately)

**Status:** done

- [x] `BackupSizeAnomalyDetector` motoru geliştirildi; son 10 başarılı yedeğin ortalama ($\mu$) ve standart sapmasını ($\sigma$) hesaplar.
- [x] Soğuk başlangıç (cold start): Bir işin 3'ten az başarılı yedeği varsa anomali hesaplanmaz, durum `BaselinePending` olarak işaretlenir.
- [x] Boyut sapması >2σ VE >%30 olduğunda (çift yönlü: beklenmedik düşüş ya da aşırı artış) `SizeAnomalyWarning` advisory durumu üretilir.
- [x] Anomali tespit edilse dahi yedekleme işlemi (`BackupRun` / `BackupAttempt`) asla başarısız sayılmaz veya iptal edilmez; `Succeeded` olarak tamamlanır.
- [x] Türetilmiş `IsSizeAnomaly` ve kalıcı durum/ortalama/sapma metadata'sı REST API ile sunulur.
- [x] Anomali, advisory audit kaydı ve politika tarafından kabul edilen N1 outbox event/delivery kayıtları aynı transaction'da yazılır; email/webhook aktarımı ayrı worker'dadır.
- [x] Koşu detayı ve listesinde `Boyut Anomalisi` rozeti ve geçmiş ortalama karşılaştırması gösterilir.
- [x] Integration testlerinde cold start, normal dalgalanma, düşüş/şişme, idempotency, transaction rollback ve loopback SMTP/webhook senaryoları geçti.

## Handoff & Technical Decisions for Next Session

1. **Single Source of Truth (`IsSizeAnomaly`):**
   - Veritabanında ayrı bir `IsSizeAnomaly` boolean kolonu tutulmayacak; `SizeAnomalyStatus` ("BaselinePending", "Normal", "SizeAnomalyWarning") tek kaynak olacak.
   - `IsSizeAnomaly => SizeAnomalyStatus == BackupSizeAnomalyStatuses.SizeAnomalyWarning` domain ve view katmanında türetilecek.
   - Veritabanında saklanan sütunlar: `SizeAnomalyStatus`, `SizeAnomalyDeviationRatio`, `SizeAnomalyBaselineBytes`.

2. **Idempotency & Entegrasyon Seam (`PostgreSqlBackupRunStore.MutateAsync`):**
   - Değerlendirme `MutateAsync` içinde, `!wasTerminal && run.Status == BackupStatus.Succeeded` koruması altında yapılacak (çift çağrılarda veya idempotent re-complete işlemlerinde tekrar çalışmayacak).
   - Baseline sorgusu `prepare` aşamasında veya mutate içinde aynı işlem altında çekilecek: `Where(r => r.TenantId == tenantId && r.JobId == jobId && r.Status == BackupStatus.Succeeded && r.Id != run.Id).OrderByDescending(r => r.CompletedAt ?? r.CreatedAt).Take(10)`.

3. **N1 Bildirim Entegrasyonu:**
   - Ajan tamamlama isteğini yavaşlatmamak için doğrudan inline sweep çalıştırılmayacak.
   - `PostgreSqlNotificationObservationSource` içinde tek seferlik (`isCondition: false`) ve `backup_size_anomaly:{runId:N}` anahtarlı gözlem kaynağı (`SizeAnomaliesAsync`) eklenecek.
   - Periyodik `NotificationDetectionWorker` ve testlerde `_world.Engine().SweepAsync()` tarafından toplanacak.

4. **EF Core Migration:**
   - Migration elle değil `dotnet ef migrations add BackupSizeAnomalyDetection` ile üretilecek (böylece `.Designer.cs` ve `ModelSnapshot` uyumlu güncellenecek).

## Verification

- `05ccf1ac`, `d69ef6e`: uygulama; `03d53a9`: tüketici regresyon kontrolleri.
- İzole PostgreSQL üzerinde ilk bütünleşik koşuda tüm `BackupSizeAnomalyIntegrationTests` senaryoları geçti; aynı koşudaki yedi hata sağlık fixture/veritabanı kapasite constraint sorunlarına aitti.
- Çalışan gerçek HTTPS API'de protokol istemcisiyle üç 1.000.000.000 bayt baseline + bir 600.000.000 bayt completion: dört koşu `Succeeded`, son koşu `SizeAnomalyWarning`, sapma -0,4.
- Gerçek Chromium koşu detayında `Başarılı` + `Boyut Anomalisi` ve geçmiş ortalama/sapma görsel olarak doğrulandı. Bu smoke gerçek veritabanı motoru/backup dosyası transferi değil, gerçek HTTP/persistence/UI yoludur.
- Önceki handoff'un yalnız sweep ile outbox üretme yaklaşımı atomik gereksinim için değiştirildi: completion içinde mevcut N1 politika/dedup işlemi aynı transaction'a katılır; sweep yalnız bir defalık gözlem/recovery yoludur.
