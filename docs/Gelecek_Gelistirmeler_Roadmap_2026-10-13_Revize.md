# Genel Backup Yönetim Sistemi — İleri Geliştirmeler ve Roadmap

## 1. Amaç

Bu belge MVP sonrasında eklenen ve eklenecek özelliklerin sırasını, mevcut kod durumunu ve mimari yönünü tanımlar. 13 Ekim 2026 itibarıyla tamamlanan dilimler tarihsel tasarım kararları korunarak durum notlarıyla işaretlenmiştir. **Satış modeli yüz yüze/doğrudan satış ve manuel plan atamasıdır; uygulama içi ödeme veya billing provider gerekmemektedir.**

Temel prensip:

- MVP'deki domain modeli korunmalıdır.
- Yeni backup engine'ler modül olarak eklenmelidir.
- Yeni storage provider'lar modül olarak eklenmelidir.
- Windows/Linux farkları Agent.Core'a yayılmamalıdır.
- Database engine ile runtime birbirinden ayrılmalıdır.
- Gelecekteki özellikler için mevcut çalışan akış bozulmamalıdır.
- Her faz bağımsız test edilebilir ve ileride dağıtılabilir küçük geliştirme dilimleri halinde yürütülmelidir; bu belge dağıtım/production kararı değildir.
- Yönetim/managed-service katmanı (plan, quota, usage accounting, support access) backup motorundan ve storage provider'lardan ayrı bir modül olarak kalmalıdır (bkz. Bölüm 2A). Ödeme tahsilatı platform işlevi değildir.

---

# 1A. Durum Özeti

**Durum tarihi:** 2026-10-13. **Kod referansı:** [`main` — `c6f66b5c764eef8081a3dd936d1778eda80d1a09`](https://github.com/mtsarac/backup-platform/tree/c6f66b5c764eef8081a3dd936d1778eda80d1a09). Bu bölüm kod ve test kapsamını özetler; henüz çalıştırılmamış gerçek Windows/Google Drive kabul testlerini geçmiş saymaz.

| Etiket | Anlam |
|---|---|
| **Completed** | İlgili temel özellik kodda ve testlerde var; belirtilen kapsam dışında kalan gelişmiş işler ayrı izlenir. |
| **Partial** | Çalışan bir alt kapsam mevcut, fakat fazın tüm kabul koşulları tamamlanmadı. |
| **Planned** | Henüz ürün işlevi olarak uygulanmamış veya yalnız soyutlama/iskelet bulunuyor. |
| **Next** | Sıradaki geliştirme dilimi; başlanmış veya tamamlanmış anlamına gelmez. |
| **Future** | Uzun vadeli ve tarih/öncelik kararı verilmemiş iş. |
| **Acceptance Pending** | Kod/test var, fakat belirtilen gerçek ortam kabul doğrulaması eksik. |

## Mevcut çekirdek

| Alan | Durum | Not |
|---|---|---|
| Multi-tenant backend, auth/RBAC, PlatformAdmin | Completed | Tenant izolasyonu, Owner/Admin/Operator/Viewer ve ayrı PlatformAdmin. |
| Windows/Linux Agent host, enrollment, heartbeat, capability sync | Completed (kapsamlı) | Windows Service ve Linux owner-only enrollment var; Linux systemd installer hâlâ Planned. |
| MSSQL discovery ve Full Backup | Completed | Manuel/zamanlanmış Full; verify, checksum, staging, replica doğrulama. |
| MSSQL Full Restore | Completed | API + Agent + Restore Wizard; yeni isim, cross-instance, explicit overwrite, retention pin. Differential/log chain/PITR yok. |
| Scheduling, retry, cancellation, concurrency, lease | Completed | PostgreSQL authoritative kararlar, agent çalışma sahipliği ve recovery. |
| Local/Central/Managed/SFTP/Google Drive storage | Completed (temel kapsam) | Central managed connection olarak kullanılır; gerçek Google Drive canlı API kabulü Acceptance Pending. |
| Multi-destination, replica health, repair ve quota | Completed (temel kapsam) | Canonical destinations, atomic repair quota, lease-fenced cleanup, durable quarantine. Tam 3-2-1 sınıf/lokasyon policy engine ayrı faz. |
| Retention, soft delete, verified physical cleanup | Completed | Orphan/partial cleanup ve korumalı replica/restore pin akışları. |
| Backup Browser/Download, Storage/Quota UI | Completed | Owner/Admin download, doğrulama, tenant ve PlatformAdmin yönetimi. |
| Plans, Subscriptions, Support Access/Support Restore | Completed (tanımlı kapsam) | Support session, audit, kontrollü müşteri ortamına restore; izole support SQL ortamı Future. |
| Accessible dashboard ve restore/backup wizard | Completed | PR #5 `main` içine birleştirildi; success KPI kapsamı ve warning kategori etiketleri düzeltildi. |
| Kritik olay audit'i | **Completed — A0 / A0.1, çalışma dalı** | 2026-10-14: olay/servis/transaction/actor/tenant/test matrisi Bölüm 1B'de; `backup_storage_cleanups` durable orphan cleanup ledger ile crash-safe audit; `feat/a0-audit-trail-completion`. |
| CI ve güvenlik hardening | Completed (otomatik kontroller) | `main` Fast CI başarılı; bu, gerçek Windows/Google Drive ve tüm dış servis kabul testlerinin yerine geçmez. |

## Henüz tamamlanmamış ana alanlar

**A0 audit kapanışı çalışma dalında tamamlandı** (Bölüm 1B). **Sıradaki yeni ürün özelliği:** Faz 2A — Agent/tenant/job seviyesinde gerçek byte-temelli bandwidth limiting (Bölüm 4); bu iterasyonda başlanmadı.

**Planlı:** Faz 2B zaman profilleri ve 2C pause/resume/recovery; **erken öncelikli** minimum email/webhook bildirimleri, izole MSSQL restore-test otomasyonu ve backup artifact şifreleme; MSSQL Differential/Log/PITR; File/Folder Backup ve File Restore; Windows VSS; Linux systemd installer; Docker runtime/discovery; PostgreSQL backup; S3/SMB/FTPS; provider kapasite izleme; tam 3-2-1 ve ileri audit. Ayrıntılı **teslim sırası** Bölüm 35'tedir; faz numarası öncelik demek değildir.

**Kabul bekleyen ortamlar:** Gerçek Google Drive OAuth + API ve Windows Agent service-account/MSSQL end-to-end kabulü. 2026-10-14'te açık Win11 VM'de runtime-only DPAPI probe smoke geçti (cross-process round trip, pending credential persistence, plaintext absence, corruption refusal, delete); bu dar kabul, tam Windows suite'i veya MSSQL backup/restore kabulü değildir. VM'de .NET 10.0.12 ve SQL Server çalışıyor; SDK ve kurulu Agent service yok.

**Belge kullanımı:** Aşağıdaki fazlardaki “yapılacaklar” maddeleri tarihsel gereksinimleri de içerir. Bir fazın kodda tamamlanması maddelerin silinmesi anlamına gelmez; durum notları uygulanan kapsamı belirtir. **Teknik iş kimliği `Faz 1–30`, managed-service dilimi `M0–M8`, yatay MVP kapanışı `A0`** şeklindedir. `# 3. Faz 1` biçimindeki başlıklarda baştaki sayı **belge bölümü**, ikinci sayı **faz kimliğidir**. Mevcut referansların kırılmaması için başlıklar yeniden numaralandırılmaz. Bölüm 35 tek aktif öncelik listesidir.

## Durum kanıtının sınırı

`main` kaynak kodu, geçerli testlerin varlığı ve GitHub Fast CI sonuçları kontrol edildi; bu belge güncel tam test suite veya canlı dış servis kabulünü yeniden çalıştırmış sayılmaz. Kullanıcının sağladığı `plan-degerlendirme-2026-10-13.md` raporu **inceleme girdisidir**, koddan bağımsız doğruluk kaynağı değildir.

---

# 1B. Değerlendirme Raporundan Alınan Düzeltmeler ve MVP Audit Borcu (A0)

## Koda karşı doğrulanan düzeltmeler

- **Faz 14 required replica / primary seçimi zaten mevcut.** `BackupConfiguration.RequiredVerifiedReplicaCount`, `BackupDestination.IsPrimary`, `BackupReplicationEvaluator` ve testleri var. Bunlar raporda bulunamadığı için yeni özellik/bug sayılmamalı; **tam 3-2-1**, farklı storage sınıfı ve off-site kuralları hâlâ Faz 15'tedir.
- **Ownership/QuotaScope veri modeli zaten mevcut.** `StorageConnection.Ownership`, `StorageConnection.QuotaScope`, `QuotaScopeRule` ve `QuotaScopeRuleTests` mevcut. Kullanım projeksiyonu/admission ile uçtan uca birleşimini hedefli entegrasyon testiyle doğrulamak değerlidir; eksik implementasyon diye kaydedilmez.
- **Gerçek SFTP sağlayıcı smoke testi** önceki hardening doğrulamasında raporlandı. **Canlı Google Drive** ve **Windows DPAPI/MSSQL VM** ayrı `NOT RUN` olarak kalır. Bunlar yeni ürün geliştirme değil, kabul/doğrulama kapsamıdır.
- Repair recovery'deki `.Take(50)` tek başına starvation kanıtı değildir; discovery paginasyonu ile lease-recovery batch davranışları farklıdır. Büyük backlog altında ilerleme testi başarısız olmadıkça bug olarak işaretlenmez.

## A0 / A0.1 — Kritik operasyon audit'ini tamamlama ve orphan crash hardening (**Completed — 2026-10-14, çalışma dalı**)

Mevcut append-only `AuditEvent` altyapısı genişletildi; platform, membership, quota, storage/secret, support ve download kayıtları yeniden geliştirilmedi. Kabul dalı `feat/a0-audit-trail-completion` (`main` hedefli PR; merge yapılmadı). Aşağıdaki matris canlı ürün entry point'lerini kapsar, yeni provider veya lifecycle API'si oluşturmaz.

| Olay | Yazan servis/store | Atomiklik / idempotency sınırı | Actor / tenant | Kabul kanıtı |
|---|---|---|---|---|
| Login başarı / başarısızlık; explicit logout | `AuthenticationService`, `PostgreSqlIdentityStore` | Session insert + audit aynı save; session delete + audit aynı transaction; tekrar logout yeni event üretmez | Başarı/logout `User`; başarısız istek `Anonymous`, doğrulanmamış hedef kullanıcı actor sayılmaz; auth tenant-global | `AuditTrailCompletionIntegrationTests`, `IdentityAuditSafetyIntegrationTests`: gerçek HTTP, null/invalid input, sır saklama, audit failure rollback ve concurrent logout |
| Enrollment token create/revoke | `PostgreSqlAgentEnrollmentStore` | Token mutation + audit aynı save, token concurrency token'ı duplicate revoke'ü engeller | Gerçek `TenantAdministrator`, token'ın tenant'ı | Lifecycle HTTP testi, başka tenant deny ve tekrar revoke |
| Agent enroll, credential rotate/confirm, revoke | Enrollment / registry store | Enrollment consume + agent/credential + audit transaction; rotation/revoke agent row lock ve transaction; aynı confirmation/revoke tek event | Enroll/rotation `System`, agent hedefi ve token creator referansı; revoke gerçek admin; tenant-qualified | `AgentAuditSafetyIntegrationTests`: iki aşamalı credential güncellemesinde audit failure rollback; lifecycle replay ve sır saklama |
| Job create/edit/enable-disable, schedule/retention değişikliği | Run / schedule store, `BackupAudit` | Agent lock + transaction; tam değer karşılaştırması, no-op edit event yazmaz | API'den gerçek `User`; worker/store işlemi `System`; job tenant'ı | `BackupAuditSafetyIntegrationTests`: operator allow/deny, foreign tenant, zone/grace/policy no-op, mutation rollback |
| Manual trigger, manual/automatic retry, cancellation | `PostgreSqlBackupRunStore` | Run/attempt ile aynı transaction; idempotency key, current attempt ve committed state geçişi | İnsan isteği `User`, otomatik retry `System`; run/attempt/job ID ve tenant | Aynı key ile concurrent trigger, repeated cancel/retry, running ve retry-pending cancellation; audit failure rollback |
| Scheduled karar/trigger; attempt/run terminal sonuç | Schedule / run store, `BackupAudit` | Occurrence uniqueness ve transaction; tamamlanan attempt/run geçişi yalnız bir kez | `System`; occurrence/run/attempt/job ve tenant; closed result/failure codes | Scheduled replay, missed/overlap/admission kararı, queued expiry, claim/sweep expiry ve callback replay |
| Retention soft delete, manual delete/cancel; physical completion/failure | Retention / agent-deletion store ve service | Artifact lock veya lease/xmin fencing; mutation + audit transaction; yalnız doğrulanmış absence sonrası physical completion | Manual admin, worker/agent callback `System`; artifact/replica tenant'ı | `RetentionAuditIntegrationTests`, `AgentReplicaDeletionAuditTests`: double trigger, wrong tenant, stale owner, safe retry, rollback, gerçek local dosya deletion/absence |
| Durable orphan/quarantine ve stale repair reconciliation | `PostgreSqlReplicaRepairStore` | Replica/repair mutation + audit aynı transaction; existing replica ve lease kontrolü replay'i engeller | `System`; artifact/replica/connection ID, tenant; path/key yok | Owned orphan/unowned quarantine, conflict fencing, audit rollback ve duplicate recovery |
| Enumerated leftover sweep | `BackupStorageSweeper`, retention store | `backup_storage_cleanups` durable ledger ile fiziksel silme öncesi intent kaydı; crash/restart sonrası absence doğrulaması ve atomik terminal audit; remote I/O sırasında DB transaction tutulmaz | `System`, doğrulanan managed layout tenant/run/job/attempt | `StorageSweeperIntegrationTests`, `RetentionAuditIntegrationTests`, çapraz-proses crash/recovery smoke: remote silme sonrası crash olsa bile audit tamamlanır, duplicate execution idempotently yutulur |
| Storage/secret; platform/membership/quota | Mevcut storage ve platform administration store'ları | Mevcut mutation/audit transaction ve permission sınırları korunur | Mevcut yetki türü ve tenant-qualified metadata | Mevcut storage invariant/secret ve platform/managed-service testleri korunmuştur; ikinci audit yolu eklenmedi |
| Support/download; restore request/cancel/fallback/terminal | Mevcut support/download store'ları; `PostgreSqlRestoreStore` | Fail-closed mevcut sözleşme korunur; restore terminal/pin release audit ile atomik ve replay-safe | Support gerçek PlatformAdmin/session; normal request `User`, terminal/fallback worker `System`; tenant | `RestoreAuditIntegrationTests`: success/failure audit rollback, pin, repeat completion; mevcut support/download güvenlik testleri korunur |

**Semantik sınırlar:** Job hard-delete entry point'i yoktur; mevcut `BackupJob.Disable()` logical deletion/disable olarak audit edilir. Agent disable/enable yalnız domain/test davranışıdır, canlı yönetim entry point'i yoktur; olmayan olay için yeni API veya audit yazıcısı eklenmedi. Logout, sunucunun gözlediği explicit session revocation'dır; pencere kapanması veya cookie expiry logout sayılmaz.

**Ortak güvenlik:** Audit tracked writes append-only; scoped context yalnız kendi tenant'ını okuyabilir, tenant yoksa fail-closed. Yetkili PlatformAdmin global/tenant-filtered liste okuyabilir. Yeni payload'lar identifier, bounded configuration ve closed result/code alanlarıyla sınırlıdır; password, token/credential değeri veya credential hash'i, storage key/path, SQL host ve arbitrary exception message içermez. Başarısız state mutation'ları successful transition diye audit edilmez; authentication failure ve scheduled decision kendi güvenli sonuç olaylarını taşır.

**Doğrulama:** Release build (`ContinuousIntegrationBuild`, warnings-as-errors); unit **3721**, architecture **123**, Fast Agent **361** PASS. Odaklı A0 + orphan ledger + GoogleDrive + StorageConnection PostgreSQL testleri **51/51 PASS**; PostgreSQL template isolation **4/4 PASS**. Frontend **55 dosya / 532 test PASS**, typecheck/lint/build PASS. Idempotent migration scriptleri uygulandı; pending model change yok. Gerçek çapraz-proses crash/recovery/replay smoke testi PASS (gerçek local dosya silinmesi sonrası proses exit, restart'ta absence verification ve tek audit kaydı).

**Windows dar smoke:** Açık Win11 VM'de mevcut derlenmiş probe ve .NET 10 runtime ile DPAPI cross-process round trip, pending rotation credential persistence, plaintext absence, corruption refusal ve deletion **PASS**. SDK, Agent service veya başka kullanıcı kurulmadı; geçici bundle/identity dizini temizlendi. Full Windows account/service ve MSSQL end-to-end kabulü çalıştırılmadı.

**Test geliştirme döngüsü:** PostgreSQL şeması process başına bir kez migrate edilip her fixture'a ayrı DB klonlanır; veri izolasyonu veya test kapsamı paylaşılmaz. Frontend `vmThreads` ile jsdom kurulumu tekrar kullanılabilir, file isolation korunur. `scripts/integration/run-backend-tests.sh --project … --filter …` ve PostgreSQL runner'ın explicit `--filter` seçimi hızlı geri bildirim sağlar; full acceptance ayrı ve isteklidir.

**Açık doğrulama açığı (NOT VERIFIED):** Tam PostgreSQL suite'i (1.580 test; yerel/CI filtresi MSSQL ve `BackupPipelineSmoke` hariç) **30 dakikalık zaman aşımına ulaştı ve hiç tamamlanmadı**; tamamı için PASS iddiası yoktur ve final entegrasyonda yeniden çalıştırılmadı. Kapsam yerine kanıt dilimleriyle doğrulandı: unit, architecture, Fast Agent PASS; final entegrasyonda A0 + orphan ledger + identity/agent/restore/retention/sweeper + GoogleDrive + StorageConnection + fixture isolation odaklı PostgreSQL seçimi **165/165 PASS**; idempotent migration scripti boş veritabanına uygulandı, son migration'ın `Down` → yeniden `Up` turu ve pending-model kontrolü PASS. Final incelemede `DurableOrphanCleanupLedger.Down` içinde audit `ActorKind` check'ini `(1, 2, 3)`'e daraltan artık satırlar bulundu ve kaldırıldı (önceki migration `Down` bilinçli olarak genişletilmiş check'i korur; 4/5 değerli append-only kayıt varken rollback kırılırdı). Canlı Google Drive OAuth/API ve Windows MSSQL end-to-end kabulü A1 sınırıdır. **A0 / A0.1 sonrası Faz 2A otomatik başlatılmadı.**

**Ayrı takip işi — PostgreSQL suite süresi (ölçülmedi, ürün özelliği değildir):** Süre nedeni henüz profillenmedi; aşağıdakiler doğrulanmamış hipotezlerdir ve ilk adım ölçümdür. (1) Önce `--logger trx` ile sınıf/test bazlı süre dağılımı çıkar. (2) `run-postgres-tests.sh` `xUnit.MaxParallelThreads=2` ile çalıştırır; tek paylaşımlı PostgreSQL üzerinde koleksiyon paralelliği ve konteyner I/O sınırı ölçülmeli. (3) ~98 sınıf kendi `PostgreSqlFixture` veritabanını `CREATE DATABASE … TEMPLATE` ile klonlar; sınıf sayısı × klon maliyeti ve template bağlantı beklemesi ölçülmeli. (4) Entegrasyon testlerinde 31 `Task.Delay`/`Thread.Sleep` kullanımı gerçek-zamanlı bekleme olarak incelenmeli (deterministik saat/orchestration). (5) Uzun testler için `--blame-hang-timeout` ile takılma ve gerçek yavaşlık ayrıştırılmalı. Kabul: test izolasyonu/kapsamı azaltılmadan, tam PostgreSQL suite'in CI zaman bütçesinde tamamlandığı ölçülü kanıt; sonuçlar `NOT RUN`/`PASS` olarak ayrı raporlanır.

## Geliştirme hijyeni ve belge güvenliği

- `docs/` Git `.gitignore` kapsamındadır; planın branch/PR geçmişi istenirse **yalnız gözden geçirilmiş `.md` dokümanları** için açık allowlist veya güvenli bir `planning/` dizini kullan. `git add -f docs/` veya tüm dizini stage etme: yerelde hassas credential/secret adları taşıyan dosyalar bulunabileceği raporlanmıştır; içerikleri paylaşma veya depoya taşıma.
- Her yeni büyük faz için **tasarım → güvenlik/race/failure incelemesi → deterministik + DB/provider testleri → acceptance** küçük kapanış kapısı tanımlanır. Önkoşullar, kabul ölçütleri ve `NOT RUN` açıkça yazılır; CI green tek başına gerçek provider kabulü değildir.
- Ürün operasyonel gereksinimleri için ayrı, dağıtım talimatı olmayan backlog: tenant veri silme/KVKK veri yaşam döngüsü; platformun **kendi PostgreSQL metadata DB'si** için yedek+geri yükleme; backend–agent protokol sürüm/uyumluluk penceresi; logs/metrics/tracing ve başarısız job/agent health izlenebilirliği. Geliştirme dışında prod/staging kararı üretmez.

---

# 2. Hedef Modüler Mimari

Uzun vadeli yapı:

```text
Platform
├── Domain
├── Application
├── Contracts
├── Infrastructure
│
├── Identity
├── Tenancy
├── Agents
├── Scheduling
├── Backups
├── Restore
├── Storage
├── Audit
├── Notifications
├── Reporting
├── ManagedService        (Plans, Subscription, Quota, Usage, Support Access — bkz. Bölüm 2A)
│
├── Agent.Core
├── Agent.Platform.Windows
├── Agent.Platform.Linux
│
├── Database Providers
│   ├── MSSQL
│   └── PostgreSQL
│
├── Database Runtime
│   ├── Native
│   └── Docker
│
├── FileSystem Providers
│
├── Storage Providers
│   ├── Local
│   ├── Managed           (Central provider'ın PlatformManaged target yapılandırması; bkz. 2A.12)
│   ├── Central
│   ├── SFTP
│   ├── SMB
│   ├── FTPS
│   ├── FTP               (legacy, güvenlik uyarısıyla)
│   ├── Google Drive
│   ├── S3
│   ├── OneDrive
│   ├── Dropbox
│   └── Azure Blob
│
├── Snapshot Providers
│   ├── Windows VSS
│   ├── Linux LVM
│   ├── Btrfs
│   └── ZFS
│
├── Notification Providers
│   ├── Email
│   ├── Teams
│   ├── Slack
│   └── Telegram
│
└── Secret Providers
    ├── Data Protection
    ├── Vault
    └── Cloud Secret Managers
```

---

# 2A. Managed Backup Service / Commercial Layer

**Durum: Completed (M0–M6 temel kapsamı); M7 Partial; M8 Future.** Plan/subscription, kota/usage, storage connections, download, Restore UI ve Support Access/Support Restore mevcut. M7'nin SFTP ve Google Drive kısımları uygulanmış, SMB/FTPS/FTP/S3 beklemektedir. M8'de tarif edilen **izole support restore ortamı** henüz yoktur: bugünkü support restore müşterinin seçilen SQL hedefine gider ve aynı özellik sayılmamalıdır. **Online billing ve M9 aktif kapsamdan çıkarılmıştır**; uygulama yüz yüze/doğrudan satılır ve PlatformAdmin aboneliği elle yönetir. Bu bölüm teknik roadmap'in (Faz 1–30) yerini almaz.

## 2A.1 Amaç ve sınır

Managed Service Layer, platformun ticari olarak işletilmesini sağlar: tenant'a kapasite paketi atama, kullanım takibi, kullanıcıya backup geçmişi/indirme sunma ve destek ekibinin kontrollü erişimi.

```text
Managed Service Layer
├── Plans
├── Tenant Subscription
├── Storage Quota
├── Storage Usage Accounting
├── Storage Connections
├── Backup Browser / Download
├── Support Access
└── Support Restore
```

Bu katman backup motorundan ayrı kalır:

```text
Backup Orchestration ── admission sorusu ──▶ IBackupAdmissionPolicy (contracts)
                                                      ▲
                                                      │ uygular
                                              ManagedService (Quota)

Storage Provider'lar ── replica SizeBytes ──▶ Usage projection (ManagedService)
```

- Backup orchestration yalnız dar bir karar arayüzü görür: `allowed` veya `denied(reasonCode)`. Plan, tier, quota, ödeme kavramlarını bilmez.
- Storage provider'lar quota, plan veya billing bilmez. Yalnız mevcut yeteneklerini (capability) ve upload/download işlemlerini sağlar.
- Agent, quota kararı vermez.
- Yeni plan, yeni quota politikası veya yeni provider eklemek core orchestration'ın yeniden yazılmasını gerektirmez.

## 2A.2 Plan / tier modeli

İlk ticari model 1 GB / 5 GB / 10 GB / 20 GB kapasite paketleridir. Bunlar kodda sabit değil, **veridir**.

```text
Plan
- Id
- Name
- Enabled
- StorageLimitBytes        (null = limitsiz)
- QuotaPolicyId            (varsayılan quota politikası)
- PolicyConfiguration      (opsiyonel, versiyonlu; ileride feature/limit eklemek için)
```

- 1/5/10/20 GB seed veridir; enum veya `switch` ile temsil edilmez.
- 50 GB, 100 GB, unlimited veya custom enterprise plan eklemek yeni bir `Plan` satırıdır; migration veya core değişikliği gerektirmez.
- Tenant'a özel istisna `TenantSubscription.StorageLimitOverrideBytes` ile verilir, yeni plan üretmek zorunda kalınmaz.
- Limit her zaman byte olarak saklanır. **1 GB = 1.000.000.000 byte.** UI ticari kapasiteyi decimal GB olarak gösterir; GiB gerekiyorsa ayrı ve açık biçimde `GiB` etiketiyle gösterilir (bkz. 2A.24). 1/5/10/20 GB planlarının byte değerleri 10⁹'un katlarıdır.

## 2A.3 Tenant Subscription

```text
TenantSubscription
- TenantId
- PlanId
- Status                       (Active, Suspended, Expired ...)
- StartsAt / EndsAt            (EndsAt opsiyonel)
- StorageLimitOverrideBytes    (opsiyonel)
- AssignedByPlatformAdminId
```

- Plan satışı, fiyat teklifi, sözleşme ve tahsilat uygulama **dışında/yüz yüze** yönetilir; PlatformAdmin tenant'a ilgili planı elle atar. Uygulamada ödeme alma ve payment gateway yoktur.
- Plan değişiklikleri geçmişi korur; tenant başına aynı anda tek aktif subscription vardır.
- Plan atama/değiştirme audit edilir.
- Downgrade sonrası kullanım yeni limitin üstünde kalırsa tenant `OverQuota` olur. Mevcut backup'lar otomatik silinmez.
- Backup domain'i ödeme mantığı bilmez; yalnız subscription durumunu (Active/Suspended) quota katmanı üzerinden görür.

## 2A.4 QuotaPolicy — quota scope hardcoded değildir

Quota "yalnız managed storage için" varsayımıyla tasarlanmaz. Hangi replica'ların sayılacağı **policy verisidir**.

```text
QuotaPolicy
- Id / Name / Version
- AccountingMode           (LogicalArtifactBytes | PhysicalReplicaBytes)
- IncludedStorageScopes    (hangi storage target'lar sayılır; seçici kural kümesi)
- EnforcementMode          (BlockNewBackups | WarnOnly | None)
- WarningThresholdPercent  (varsayılan 80)
- CriticalThresholdPercent (varsayılan 90)
```

Limit değeri `Plan` veya subscription override'ından gelir; policy limiti değil, **nasıl sayılacağını** tanımlar.

`IncludedStorageScopes` kuralı şu özelliklerden herhangi biriyle seçim yapabilmelidir: tümü, `ProviderType` kümesi, `OwnershipType` kümesi, açık `QuotaScope` etiketi, tek bir storage connection.

Örnek politikalar (hepsi veri; kod değişikliği yok). Örnek artifact: 2 GB. Replica'lar: Managed (`PlatformManaged`), Google Drive (`TenantManaged`), SFTP (`TenantManaged`).

| Policy | IncludedStorageScopes | AccountingMode | Hesaplanan kullanım |
|---|---|---|---|
| **Default (ilk ticari policy)** | All | LogicalArtifactBytes | **2 GB** |
| Her fiziksel kopya ayrı sayılır | All | PhysicalReplicaBytes | **6 GB** |
| Yalnız platform-managed hedefler | `OwnershipType = PlatformManaged` veya `QuotaScope = managed` | Logical veya Physical | 2 GB |
| Seçili provider tipleri | `ProviderType ∈ {Central/Managed, GoogleDrive}` | Physical | 4 GB |
| Quota yok | — | `EnforcementMode = None` veya limitsiz plan | Sayılmaz, state `Unlimited` |

Logical ve Physical farkı: aynı artifact 3 hedefe yazılsa da `LogicalArtifactBytes + All` onu bir kez sayar (2 GB); `PhysicalReplicaBytes + All` her kopyayı ayrı sayar (6 GB).

Scope farkı: artifact yalnız SFTP'de duruyorsa (2 GB), `All` ile 2 GB sayılır, yalnız-`PlatformManaged` policy'siyle 0 GB sayılır. Sonuç koddan değil, policy'nin seçtiği scope kuralından çıkar.

Politika değişince kullanım projection'ı yeniden hesaplanır (bkz. 2A.6). Hangi policy versiyonunun uygulandığı projection'da saklanır.

### Default (ilk) policy — ürün kararı

```text
AccountingMode           = LogicalArtifactBytes
IncludedStorageScopes    = All
EnforcementMode          = BlockNewBackups
WarningThresholdPercent  = 80
CriticalThresholdPercent = 90
```

- İlk ticari davranışta Local, Central/Managed, SFTP, Google Drive ve gelecekteki diğer storage target'lar quota kapsamına girebilir; default policy `All` kullanır.
- Bu değerler hardcode değildir; seed policy verisidir.
- Gelecekte yalnız `PlatformManaged` storage sayılmak istenirse kod/domain değişmez: policy'nin `IncludedStorageScopes` kuralı güncellenir ve usage yeniden hesaplanır (2A.6).
- `OwnershipType` quota kararının kaynağı değildir; default policy ownership'e bakmaz.
- Müşteri-owned hedeflerin de sayılması ticari olarak tartışılabilir bir tercihtir; policy ile geri alınabilir.

## 2A.5 Storage target accounting özellikleri

> Buradaki quota scope / billing scope terimleri **depolama kullanımının teknik kapsamını** ifade eder; çevrimiçi ödeme, tahsilat veya fatura entegrasyonu anlamına gelmez.

Bir storage target (Local, Central, Managed, SFTP, FTPS, FTP, SMB/NAS, Google Drive, OneDrive, Dropbox, S3, Azure Blob ve gelecekteki provider'lar) quota kapsamına girip girmediğini **açıklayabilmelidir**:

```text
StorageTarget
- ProviderType      (SFTP, GoogleDrive, S3 ...)
- OwnershipType     (PlatformManaged | TenantManaged | External)
- QuotaScope        (BillingScope; açık etiket, örn. "managed", "tenant-cloud")
```

- Quota kararı doğrudan `OwnershipType`'a bağlanmaz. Ownership, `QuotaScope` için yalnız varsayılan değer üretir; kararı `QuotaPolicy.IncludedStorageScopes` verir. Böylece tenant-owned Google Drive istenirse kapsama alınabilir.
- Default policy `IncludedStorageScopes = All` kullandığı için ilk ticari davranışta ownership/scope hiçbir hedefi dışarıda bırakmaz. Bu özellikler quota kararının kaynağı değil, yalnız ileride daraltıcı policy'lerin kullanabileceği seçici verilerdir.
- Bu özellikler storage connection/target konfigürasyonunda tutulur, provider koduna gömülmez.
- Kullanım, provider'ın bildirdiği doluluk bilgisinden değil, platformun kaydettiği replica boyutlarından hesaplanır. Böylece provider `SupportsCapacityQuery` desteklemese de her provider quota kapsamına alınabilir. (Faz 21'deki provider kapasite izleme ile karıştırılmamalıdır.)
- Mevcut `BackupReplica` modelinde `StorageProvider`, `StorageTarget` ve `SizeBytes` alanları bulunur; `BackupArtifact` `SizeBytes` taşır. Hangi ek referansın (connection id, scope) gerektiği implementasyon öncesi mevcut şemaya karşı doğrulanmalıdır.

## 2A.6 Usage accounting — logical vs physical

İki modelin ikisi de domain'de desteklenir; hangisinin kullanılacağı `QuotaPolicy.AccountingMode` ile seçilir.

```text
LogicalArtifactBytes:
  usage = Σ artifact.SizeBytes
          (≥1 kapsamdaki sayılabilir replica'sı olan artifact'lar; aynı artifact bir kez)

PhysicalReplicaBytes:
  usage = Σ replica.SizeBytes
          (kapsamdaki tüm sayılabilir replica'lar; her fiziksel kopya ayrı)
```

**Default MVP commercial accounting: logical artifact usage.** Bu varsayım policy ile değiştirilebilir; kodda sabit değildir.

Sayılabilir replica durumu (ürün kararı): fiziksel olarak mevcut olanlar. `Available`, `DeletePending` ve **`Quarantined`** nesneler ilgili policy kapsamında tutuldukları sürece muhasebeden düşülmemelidir; quarantined hedef sahipliği doğrulanmadan silinmez, restore/download kaynağı sayılmaz. Usage yalnız fiziksel silme güvenle doğrulandığında düşer (agent-local replica'da silme agent tarafından doğrulanana kadar sayım sürer). Soft delete tek başına usage'ı düşürmez. Yarım kalmış upload'lar için reservation/durable orphan muhasebesi ve recovery uygulanır; yalnız metadata yokluğuna bakarak fiziksel kapasite serbest varsayılmamalıdır.

### TenantStorageUsage (read model / projection)

```text
TenantStorageUsage
- TenantId
- UsedBytes
- ReservedBytes
- CalculatedAt
- (opsiyonel) BackupCount, AccountingMode, PolicyVersion
```

- Her API isteğinde tüm artifact tablolarını `SUM` etmek zorunda kalınmaz.
- **Projection source of truth değildir.** Gerçek kaynak artifact/replica kayıtlarıdır.
- Güncelleme replica commit ve silme-tamamlanma anlarında, aynı transaction'da veya idempotent olay ile yapılır.
- Periyodik reconcile kaynak tablolarla karşılaştırır; sapma düzeltilir ve loglanır.
- Tenant başına rebuild işlemi bulunur (yetkili, audit edilir).
- Policy veya subscription değişince yeniden hesaplama tetiklenir.

## 2A.7 Reserved capacity ve quota davranışı

Artifact boyutu backup başlamadan kesin bilinmeyebilir, bu yüzden `UsedBytes` ve `ReservedBytes` ayrıdır.

- **Mevcut implementasyon:** Run admission rezervasyonları ve repair operasyonlarına ait `ReservedQuotaBytes` veritabanında takip edilir; tenant kilidi ve incremental/reconcile usage hesabı kullanılır. Aşağıdaki başlangıç politikası tasarım kararı olarak korunur, ancak geçerli uygulama gerçek kod/entegrasyon testleriyle değerlendirilir. Run terminalleştiğinde rezervasyon bırakılır; retry ve crash recovery sırasında aktif operasyonların rezervasyonları korunur.
- **Zaten over quota ise** yeni backup başlatılmaz (manuel ve scheduled; admission gate `denied(quota_exceeded)` döner, sebep kullanıcıya görünür; scheduled occurrence sessizce kaybolmaz, sebep kaydedilir).
- **Çalışırken limit aşılırsa** varsayılan davranış: mevcut backup güvenle tamamlanır → tenant `OverQuota` olur → sonraki backup bloklanır. Çalışan SQL backup byte sınırında zorla kesilmez.
- **Davranış ayrımı:** Yeni backup run'larının başlangıçtaki boyut tahminleri ile gerçek çıktı arasında fark oluşabilir; başlangıç rezervasyonu, gerçek boyutun mutlak sınır garantisi değildir. **Replica repair admission** ise ek kopya maliyetini tenant kilidi altında atomik rezerve eder; bu iki akışın garantileri karıştırılmamalıdır.
- Over quota durumu şunları **engellemez**: mevcut backup'ların görüntülenmesi, download, restore, manuel silme, retention cleanup. Aksi halde tenant quota'dan çıkamaz.
- Retry (ürün kararı): admission'dan geçmiş aynı `BackupRun` altındaki retry attempt'leri quota gate'e tekrar sorulmaz. Yeni bir `BackupRun` (manuel veya scheduled) her zaman quota admission'dan geçmek zorundadır.

## 2A.8 Quota states

```text
Normal      < Warning eşiği
Warning     ≥ %80  (configurable)
Critical    ≥ %90  (configurable)
OverQuota   ≥ %100
Unlimited   limit yok veya EnforcementMode = None
```

- State, usage + limit + policy'den türetilir; bağımsız bir doğruluk kaynağı değildir.
- Eşikler `QuotaPolicy` içinde configurable'dır.
- UI bu state'leri doğrudan kullanır. State değişimleri notification event'i üretebilir (provider'lar Faz 24'te).

## 2A.9 Retention ve quota ayrıdır

| Kavram | Soru |
|---|---|
| Retention | Bu backup ne kadar süre tutulmalı? |
| Quota | Tenant toplam ne kadar veri saklayabilir? |

- Quota dolunca retention policy sessizce override edilmez; backup silinmez.
- Otomatik silme istenirse ileride ayrı ve explicit `QuotaPressurePolicy` olarak eklenir: varsayılan kapalı, tenant açık onayı gerektirir, minimum retention ve son başarılı backup korunur, audit edilir.

## 2A.10 Storage Connections

Tenant kendi storage connection'larını tanımlar: Google Drive, SFTP, FTPS, FTP, SMB/NAS, S3-compatible, OneDrive, Dropbox ve gelecekteki provider'lar.

- Credential'lar secret provider üzerinden saklanır. Plaintext olarak DB'de tutulmaz.
- Credential frontend'e geri dönmez (write-only). API yalnız "yapılandırılmış" bilgisi veya maskelenmiş parmak izi döner.
- Oluşturma, güncelleme, secret rotasyonu, bağlantı testi, devre dışı bırakma ve silme audit edilir. Audit kaydı secret içermez.
- Bağlantı testi connection aktif olmadan önce yapılır (Faz 1'deki connection testing ile ortak).
- Replica'sı bulunan connection sessizce silinemez; explicit akış gerekir.
- OAuth tabanlı provider'larda token yaşam döngüsü Faz 19'daki gibi yönetilir; cloud token'ları Agent'a kalıcı verilmez.
- Connection tenant'a aittir; tenant isolation korunur, bir tenant başka tenant'ın connection'ını göremez veya kullanamaz.
- Her connection `OwnershipType` ve `QuotaScope` taşır (bkz. 2A.5).
- Plain FTP connection'ı platform seviyesinde `Disabled` iken oluşturulamaz; etkinleştirilmişse oluşturma güvenlik uyarısı ve explicit confirmation ister (bkz. 2A.12).

## 2A.11 Multi-destination backup

Mevcut model korunur:

```text
BackupArtifact
    ↓
BackupReplica
```

```text
Artifact
├── Managed Storage
├── Google Drive
└── SFTP
```

- **Implemented:** Artifact birden fazla replica taşıyabilir; canonical `destinations` listesindeki `isPrimary` ve `requiredVerifiedReplicaCount` aktif kullanılır (Faz 14 temel kapsamı).
- Quota accounting seçili policy'ye göre bu replica'ları sayar veya hariç tutar (2A.4, 2A.6).
- UI, her destination için "bu hedef quota'ya sayılır mı?" bilgisini gösterir.
- Kısmi başarı durumunda (bazı replica'lar başarısız) accounting yalnız gerçekten oluşmuş replica'ları sayar.

## 2A.12 Storage provider roadmap

| Provider | Mevcut durum | Tipik ownership | Not |
|---|---|---|---|
| Local | Completed | TenantManaged / External | Agent diskindeki mevcut provider. |
| Central | Completed | PlatformManaged | Merkezi storage provider. |
| Managed Storage | Completed (Central yapılandırması) | PlatformManaged | Ayrı teknik provider değil; Central storage connection'ının ownership/quota scope varyantı. |
| SFTP | Completed (temel) | TenantManaged | Network policy, bağlantı testi, upload/readback/verify, cancellation/cleanup. |
| SMB/NAS | Planned (Faz 18) | TenantManaged | Uygulama yok. |
| Google Drive | Completed (temel); Acceptance Pending | TenantManaged | OAuth ve resumable upload kodu var; canlı Google Drive kabulü eksik. Provider kapasite izlemesi ayrı Faz 21. |
| FTPS | Planned | TenantManaged | Plain FTP'ye tercih edilir. |
| FTP | Planned (legacy) | TenantManaged | Güvenlik kısıtları aşağıda. |
| S3-compatible | Planned (Faz 16) | PlatformManaged / TenantManaged | Uygulama yok. |
| OneDrive | Future | TenantManaged | OAuth cloud-drive. |
| Dropbox | Future | TenantManaged | OAuth cloud-drive. |
| Azure Blob | Future | PlatformManaged / TenantManaged | |

**Plain FTP** desteklenebilir ancak legacy olarak işaretlenir ve platform genelinde varsayılan olarak **`Disabled`**'dır:

- PlatformAdmin açıkça enable edebilir; bu değişiklik audit edilir.
- Connection oluştururken açık güvenlik uyarısı (TLS yok; credential ve veri açık gider) ve explicit confirmation zorunludur; onay audit edilir.
- FTPS veya SFTP önerilir; UI bu alternatifleri öne çıkarır.
- Provider disable edilince mevcut connection'lar sessizce silinmez; yeni connection oluşturma kapanır ve mevcut connection'ların davranışı implementasyonda açıkça tanımlanır.

Önerilen teknik öncelik (kesin zorunluluk değildir, business ihtiyacına göre değişebilir):

```text
Managed Storage
→ SFTP
→ Google Drive
→ SMB/NAS
→ FTPS
→ FTP
→ S3-compatible
→ OneDrive
→ Dropbox
→ Azure Blob
```

Her provider mevcut storage abstraction ve capability sistemi üzerinden eklenir; provider'lar birbirini bilmez. Download, resumable upload, signed URL gibi özellikler zorunlu interface değil capability/optional interface olarak açıklanır. Yeni provider core orchestration'da değişiklik gerektirmez.

## 2A.13 Backup Browser

Tenant kullanıcıları kendi backup geçmişini görür:

- artifact boyutu,
- database,
- oluşturulma zamanı,
- verification durumu,
- checksum durumu,
- retention (silinme tarihi, soft-delete durumu),
- storage replica'ları (destination, durum, quota'ya sayılıp sayılmadığı).

Yetkili kullanıcı mevcut tenant rolüne uygun işlemleri yapar: download, restore, delete / cancel-delete. İlk sürümde **download yalnız tenant `Owner` ve `Admin`** rolleriyle sınırlıdır; Operator ve Viewer indiremez. İleride ayrı permission/policy ile genişletilebilir. Restore için mevcut authorization davranışı ayrı kalır ve download kararından etkilenmez.

**Mevcut durum: Completed (M4 temel kapsamı).** Artifact/history ve tenant Backup Browser/Download arayüzleri bulunur. Provider'ların her fiziksel topolojide canlı erişimi, ayrı acceptance konusu olarak kalır.

## 2A.14 Backup download

Download explicit bir özelliktir ve şu akıştan geçer:

```text
authorization
→ tenant ownership
→ artifact/replica availability
→ secure stream / short-lived URL
→ audit
```

- Yetki: ilk sürümde yalnız tenant `Owner` ve `Admin` (2A.13). Support oturumu üzerinden indirme ayrıca `DownloadBackup` permission'ı gerektirir (2A.16).
- Storage path veya credential kullanıcıya hiçbir zaman verilmez.
- Replica seçimi: doğrulanmış, erişilebilir ve silinmemiş replica; birden fazla varsa backend seçer.
- Provider-specific download capability kullanılır (generic storage interface kirletilmez). Provider desteklemiyorsa özellik capability üzerinden devre dışı kalır.
- Central/managed için backend stream eder. Cloud provider'larda kısa ömürlü URL veya backend proxy. Agent-local replica için agent'ın çevrimiçi olması ve agent üzerinden stream gerekir; Agent-backend yönü pull/outbound olduğundan backend'in agent'a doğrudan bağlanabileceği varsayılmaz.
- Download süresince replica'nın fiziksel silinmesi engellenmelidir (mevcut restore pin mekanizması örnek alınır).
- Artifact checksum bilgisi kullanıcıya gösterilir; download sonrası kullanıcı doğrulayabilir.
- Her download audit edilir: kim, hangi artifact/replica, sonuç, aktarılan byte. Rate limiting uygulanır.
- Over quota download'u engellemez.

## 2A.15 Restore UI

**Mevcut durum: Completed (M5).** MSSQL Restore API ve Restore Wizard mevcut; aşağıdaki kullanıcı akışları implementasyonun gereksinimlerini açıklar:

- artifact'tan restore başlatma, target agent/SQL seçimi, yeni ad / overwrite modu,
- production'a overwrite için explicit onay (mevcut `confirmOverwrite` akışı),
- ilerleme, iptal, geçmiş, sonuç ve database durumu (`NotModified`, `Online`, `PartiallyRestored`, `Unknown`) gösterimi.

Durum: **Completed (temel Full Restore)**. Differential/log chain ve PITR kapsam dışıdır; mevcut backend'in Full Restore davranışı korunur.

## 2A.16 Support Access

PlatformAdmin'ın normal yetkisi müşteri backup verisine erişim anlamına **gelmez**. Mevcut Platform Admin API'si tenant, kullanıcı, membership, agent ve audit yönetimi sunar; artifact/run/backup verisi sunmaz ve bu böyle kalmalıdır. Müşteri backup verisine erişim yalnız explicit bir oturumla mümkündür.

```text
SupportAccessSession
- Id
- TenantId
- PlatformAdminId
- Reason                 (zorunlu)
- Permissions            (küme)
- StartedAt
- ExpiresAt
- Status                 (Active, Expired, Closed, Revoked)
- ClosedAt / ClosedBy
```

Permission örnekleri:

```text
ViewBackupMetadata
DownloadBackup
RestoreBackup
```

Kurallar:

- Delete gibi destructive permission varsayılan olarak verilmez.
- Oturum tek bir tenant'a bağlıdır; tenant başka tenant'a erişimde kullanılamaz.
- Her müşteri-verisi isteğinde backend oturumun aktif, süresi dolmamış ve ilgili permission'ı taşıdığını sunucu tarafında doğrular (yalnız arka plan job'ına güvenilmez).
- Süre: default 1 saat, hard maximum 4 saat (platform yapılandırması; talep edilerek hard maximum aşılamaz). Oturum sessizce extend edilmez; devam gerekiyorsa yeni gerekçeyle yeni oturum açılır.
- PlatformAdmin oturumu iptal edebilir. Tenant `Owner` ve `Admin`, kendi tenant'larındaki aktif ve geçmiş support oturumlarını (kim, gerekçe, permission, süre, durum) görebilir.
- Credential/secret değerleri support access ile bile gösterilmez.
- `ViewBackupMetadata` bile oturum gerektirir; veritabanı adları ve artifact listesi müşteri verisidir.
- Tenant approval mimari olarak desteklenebilir ancak ilk sürümde zorunlu değildir. Default policy: yetkili PlatformAdmin gerekçe + permission kümesiyle support session açabilir. İleride tenant bazlı `TenantApprovalRequired` policy'si eklenebilir (oturum akışına `PendingApproval` aşaması eklenerek; mevcut model buna kapalı değildir). Audit yazılamazsa erişim fail-closed kalır (2A.17).

## 2A.17 Support Access audit

Şu olaylar mevcut append-only audit altyapısı üzerinden kaydedilir:

```text
support session opened
support session expired / closed / revoked
artifact viewed
artifact downloaded
restore initiated
reddedilen erişim denemeleri
```

- Kayıt actor (PlatformAdmin), tenant, session id, reason ve işlem hedefini taşır; secret taşımaz.
- Audit yazılamıyorsa erişim verilmez (fail closed). Admin iz bırakmadan müşteri backup'ına erişemez.

## 2A.18 Support Restore

**Durum: Future (izole support ortamı).** Mevcut Support Access altında, müşterinin hedef SQL Server'ına sınırlandırılmış **Support Restore** akışı vardır ve UI/API üzerinden kullanılabilir; ancak bu bölümün gerektirdiği otomatik hazırlanıp temizlenen, tenant'a özel **izole support SQL ortamı** henüz geliştirilmemiştir.

```text
Artifact
→ isolated support SQL environment
→ admin inspection / repair
→ optional corrected output
→ cleanup
```

- Mevcut MSSQL Restore sisteminin üzerine kurulur, ayrı bir restore motoru yazılmaz.
- Aktif `SupportAccessSession` (`RestoreBackup`) gerektirir.
- Ortam izole, geçici ve tenant'lar arasında paylaşılmayandır; müşterinin production hedefine dokunmaz. Restore Test Automation'daki (Faz 20) izole hedef altyapısı yeniden kullanılabilir.
- Düzeltilmiş çıktı yeni artifact olarak üretilirse kaynağı (provenance) kayıtlıdır; müşteriye teslimi explicit ve audit edilir.
- Cleanup zorunludur ve doğrulanır.

## 2A.19 Customer UI — Storage

Tenant tarafında storage kullanım ekranı:

```text
Storage

7.4 GB / 10 GB
74%

Plan: 10 GB

Backups: 23
```

Kullanıcı şunları görür:

- mevcut plan,
- kullanım,
- kalan quota,
- state (Normal / Warning / Critical / OverQuota / Unlimited),
- storage destination'ları ve her birinin quota'ya sayılıp sayılmadığı,
- hesaplama modu (logical/physical) kısa açıklamasıyla.
- birim: ticari kapasite decimal GB olarak gösterilir (1 GB = 1.000.000.000 byte); GiB gösterilirse ayrı ve `GiB` etiketiyle.

OverQuota durumunda UI yeni backup'ların bloklandığını, mevcut backup'ların güvende olduğunu ve download/restore/silme işlemlerinin devam ettiğini açıkça belirtir.

## 2A.20 PlatformAdmin UI

**Mevcut durum: Completed (M3 ve M6 temel ekranları).** Platform Admin panelindeki plans/subscriptions/storage usage ve support sessions arayüzleri uygulanmıştır; aşağıdaki liste mevcut yönetim alanlarını ve ileri genişleme noktalarını tanımlar:

```text
Plans
Subscriptions
Storage Usage
Storage Connections summary
Support Sessions
```

Tenant detayında: Plan, Usage, Backup count, Storage destinations, Last successful backup, Support access. Connection özetleri secret içermez.

## 2A.21 Yüz yüze satış, manuel plan yönetimi — **online billing kapsam dışı**

**Kesin ürün kararı (2026-10-13):** Uygulama doğrudan/yüz yüze satılır. Fiyatlandırma görüşmesi, teklif/sözleşme, fatura ve tahsilat **uygulama dışında** yürütülür. Uygulama içinde ödeme sağlayıcısı, kart verisi, checkout, ödeme webhook'u, otomatik tahsilat/yenileme, fatura kesme motoru ve payment gateway **geliştirilmeyecektir**.

**Mevcut ve gerekli kalan işlev:**

1. PlatformAdmin, tanımlı 1/5/10/20 GB veya custom planı tenant'a **elle** atar, değiştirir veya bitiş tarihi tanımlar.
2. `TenantSubscription`, `Plan`, `QuotaPolicy`, `TenantStorageUsage` ve yetki/audit modeli teknik kapasite, erişim ve plan yönetimi için **korunur**; ödeme sistemine bağlanmaz.
3. Plan değişimi, süresi ve istisnalar audit edilir. Sözleşme yenilemesi/ödeme takibi uygulamaya bağımlı olmadan satış ekibince yönetilir; ödeme gecikmesi nedeni ile otomatik backup silinmez veya devre dışı bırakılmaz.
4. Gerekirse ileride **yalnız manuel referans/not** (`externalContractReference`, satış temsilcisi, sözleşme tarihi vb.) ayrı bir ürün kararıyla eklenebilir; fiyat veya kişisel ödeme bilgisi saklamak varsayılan değildir.

**Online billing provider (`M9`) aktif roadmap'ten kaldırıldı.** Gelecekte satış modeli değişirse **yeni bir ürün kararı** ile ayrı modül/integrasyon değerlendirilebilir; şimdiden soyut ödeme arayüzleri, migration'lar ve UI oluşturulmaz. Backup/domain/provider/agent hiçbiri ödeme terimi bilmek zorunda değildir.

## 2A.22 Schedule/frequency plana bağlanmaz

- Kullanıcı istediği sıklıkta backup alabilir; plan "1 GB → günlük, 5 GB → saatlik" gibi frequency'ye bağlanmaz.
- Abuse/resource protection için teknik minimum interval ayrı, platform seviyesinde bir policy olarak tanımlanabilir; ticari plandan bağımsızdır.
- İleride business isterse plan bazlı schedule limitleri `PolicyConfiguration` ile eklenebilir; domain buna kapalı olmamalıdır. Mevcut scheduling davranışı değişmez.

## 2A.23 Uzun vadeli kullanıcı fonksiyon kapsamı

Hedef, AKINSOFT benzeri yedekleme ürünlerinin temel kullanıcı fonksiyonlarını uzun vadede karşılamaktır. 2026-10-13 `main` kod durumu:

| Özellik | Durum | Not |
|---|---|---|
| Scheduled backup | Completed | MSSQL Full job'ları için. |
| Detailed history/log | Completed (çekirdek) | Run/attempt, artifact/replica, Backup Browser ve kontrollü download var; ileri raporlama Future. |
| Local / Central / SFTP / Google Drive storage | Completed (temel) | Google Drive canlı servis kabulü bekliyor. |
| MSSQL Full Restore | Completed | API + Agent + UI; file restore ve chain/PITR ayrı faz. |
| Compression | Partial | MSSQL native compression var; File/Folder artifact compression bekliyor. |
| Multi-destination ve replica repair | Completed (temel) | Quarantine, atomic admission, doğrulanmış kopya sayısı; tam 3-2-1 policy ayrı faz. |
| Quota, Plans, Usage ve Support Access | Completed (temel) | Satış yüz yüze; online billing yapılmayacak. İzole support restore ortamı ayrı Future. |
| File/Folder backup ve include/exclude | Planned | Faz 3, yalnız proje iskeleti. |
| Bandwidth limiting / transfer policies | **Next (2A)** | Faz 2A sıradaki ürün dilimi; 2B/2C ihtiyaca göre daha sonra. |
| MSSQL Differential/Transaction Log/PITR | Planned | Faz 10–12. |
| PostgreSQL backup | Planned | Faz 13. |
| S3 / SMB/NAS / FTPS | Planned | Faz 16/18 ve provider genişlemesi. |
| OneDrive / Dropbox | Future | 2A.12. |
| Email/webhook notification (minimum) | Planned — erken öncelik | Faz 24 içindeki küçük N1 dilimi; Teams/Slack/Telegram sonraya. |
| Otomatik izole MSSQL restore testi | Planned — erken öncelik | Faz 20 içindeki R1 dilimi, restore gerçekten çalışır mı kanıtı. |
| Artifact encryption | Planned — güvenlik önceliği | Yeni E1 tasarım/uygulama dilimi; özellikle üçüncü taraf storage. |

## 2A.24 Ürün kararları / başlangıç varsayımları

Önceki açık kararların tamamı kapatılmıştır. Aşağıdakiler başlangıç değerleridir. Değişim yolu "policy/config/veri" olanlar gelecekte kod veya domain değişikliği olmadan veri ya da yapılandırma güncellemesiyle değiştirilebilir; hiçbiri koda sabit yazılmaz. "Kural" olanlar domain/orchestration davranışıdır ve tek noktada tanımlanır.

| # | Konu | Karar | Değişim yolu |
|---|---|---|---|
| 1 | Storage birimi | 1 GB = 1.000.000.000 byte. Backend byte saklar. UI ticari kapasiteyi decimal GB gösterir; GiB gerekirse ayrı ve `GiB` etiketli. | Gösterim kuralı; limitler byte olduğundan veri etkilenmez |
| 2 | İlk QuotaPolicy | `LogicalArtifactBytes`, `IncludedStorageScopes = All`, `BlockNewBackups`, Warning %80, Critical %90 (2A.4). | Policy verisi |
| 3 | Managed Storage | Yeni provider değil; Central provider'ın `PlatformManaged` ownership / managed `QuotaScope` target'ı. Gerekirse sonradan ayrı provider'a ayrılabilir. | Target yapılandırması |
| 4 | `DeletePending` | Grace boyunca sayılır; usage yalnız fiziksel silme doğrulanıp `Deleted` olunca düşer (2A.6). | Accounting kuralı, tek noktada tanımlı |
| 5 | Retry | Admission'dan geçmiş run'ın retry attempt'leri gate'e sorulmaz; yeni `BackupRun` sorulur (2A.7). | Gate'in çağrıldığı nokta (run oluşturma) |
| 6 | Backup download | İlk sürümde yalnız tenant `Owner` ve `Admin`; Operator/Viewer indiremez. Restore authorization'ı ayrı kalır (2A.13, 2A.14). | Yetki policy'si; ayrı permission ile genişletilebilir |
| 7 | Support Access | Tenant approval ilk sürümde zorunlu değil. Default session 1 saat, hard maximum 4 saat, sessiz extend yok. Tenant `Owner`/`Admin` aktif ve geçmiş oturumları görür. Audit başarısızsa erişim fail-closed (2A.16, 2A.17). | Süreler platform config; `TenantApprovalRequired` ileride tenant policy |
| 8 | Plain FTP | Platform genelinde default `Disabled`; PlatformAdmin açıkça enable eder. Connection oluştururken uyarı + explicit confirmation zorunlu; FTPS/SFTP önerilir (2A.12). | Platform ayarı (audit edilir) |

Genel ilke: quota kapsamı ve accounting modu (karar 2) ile Managed Storage yapılandırması (karar 3) ownership veya provider tipine koddan bağlanmaz. Bu iki kararın ürün tarafında değişmesi core orchestration, domain veya migration değişikliği gerektirmemelidir.

## 2A.25 Modül sınırı doğrulaması

Mevcut architecture test projesine (dependency rules) ticari katman için kurallar eklenmelidir:

- Backup orchestration, domain ve provider projeleri ManagedService modülüne bağımlı olmamalı.
- Storage provider'lar quota/plan/payment tiplerine referans vermemeli.
- Agent ManagedService'e bağımlı olmamalı.

## 2A.26 Teslim dilimleri

Her dilim bağımsız doğrulanabilir şekilde geliştirilmeli; aşağıdaki “Completed” statüsü production dağıtımı yapıldığı anlamına gelmez.

| Dilim | İçerik | Durum |
|---|---|---|
| M0 | Agent Installation / Enrollment UX | Completed; Linux systemd installer ayrı Planned |
| M1 | Plans, Tenant Subscription, PlatformAdmin manuel atama ve audit | Completed |
| M2 | QuotaPolicy, usage projection/reconcile, admission, quota states ve reservations | Completed (temel); quota kapsamı/onarım güvenliği hardening ile güçlendirildi |
| M3 | Tenant Storage UI; PlatformAdmin Plans/Subscriptions/Usage UI | Completed |
| M4 | Backup Browser, Download ve ilgili audit | Completed |
| M5 | MSSQL Full Restore UI | Completed |
| M6 | SupportAccessSession, permissions, support audit ve müşteri hedefine kontrollü support restore | Completed (temel) |
| M7 | Managed Storage/Central connection ve provider genişlemesi | Partial: Managed, SFTP, Google Drive Completed; SMB/NAS, FTPS/FTP, S3 Planned |
| M8 | İzole support SQL restore ortamı (support restore'un ayrı sandbox'ı) | Future |
| M9 | Otomatik billing / payment provider | **Kapsam dışı** — yüz yüze satış, manuel plan atama; ürün kararı değişmedikçe uygulanmaz |

M2 kabul ölçütleri:

- Yeni plan, migration veya kod değişikliği olmadan veri olarak eklenebilir.
- Quota kapsamı ve accounting modu policy değişikliğiyle değişir; kod değişmez.
- Over quota tenant'ta yeni backup, görünür sebeple bloklanır; çalışan backup kesilmez.
- Over quota iken download, restore, silme ve retention cleanup çalışır.
- Projection reconcile sonucu kaynak tabloların toplamıyla eşleşir; rebuild mümkündür.
- Default policy `All + LogicalArtifactBytes`'tır. Aynı 2 GB artifact için 3 replica (Managed + Google Drive + SFTP) ile Logical + All = 2 GB, Physical + All = 6 GB sonucu doğrulanır.
- Policy yalnız-`PlatformManaged` olarak değiştirildiğinde kod değişmeden usage yeniden hesaplanır.
- Aynı `BackupRun`'ın retry attempt'leri gate'e tekrar sorulmaz; yeni run sorulur.
- `DeletePending` replica fiziksel silme doğrulanana kadar sayılır.
- Architecture testleri modül sınırını doğrular (2A.25).

---

# 3. Faz 1 — SFTP ve gelişmiş transfer

**Durum: Completed (SFTP temel provider).** MVP'deki Local/Central abstraction korunarak ilk harici storage provider eklenmiştir. Bu bölümün chunked/resumable/checkpoint gibi ileri transfer maddeleri Faz 2C ile takip edilir; tamamlandı olarak işaretlenmez.

## Yapılacaklar

- SFTP provider.
- Storage provider capability validation.
- Connection testing.
- Credential lifecycle.
- Path isolation.
- Replica verification.
- Upload retry.
- Transfer progress.
- Transfer timeout handling.

Ardından:

- chunked upload abstraction,
- resumable upload abstraction,
- transfer checkpoint metadata

eklenir.

Provider desteklemiyorsa özellik capability üzerinden devre dışı kalmalıdır.

---

# 4. Faz 2 — Bandwidth ve transfer politikaları

**Durum: Next ürün özelliği (A0 audit kapanışından sonra).** API request rate limitleri ve transfer eşzamanlılık bütçesi mevcut olsa da bunlar **byte/saniye trafik sınırlaması değildir**. Mevcut `IStorageProvider`/AgentDirect/BackendRelayed yolları incelenerek, MSSQL backup, replica repair, download/restore ve retention güvenliği bozulmadan bu faz parçalara ayrılır.

## Faz 2A — Byte-temelli upload bandwidth limiting (**sıradaki ürün dilimi; A0 sonrası**)

- Agent, tenant ve job bazlı hız sınırları için açık policy hiyerarşisi ve override kuralları (birim **bit/s veya byte/s net tanımlı**, `Unlimited` destekli).
- Aynı tenant'ın paralel upload'larının toplam sınırı aşmaması; yalnız request başına throttle uygulamak yeterli değildir.
- AgentDirect ve BackendRelayed topolojilerinde sınırın hangi noktada uygulanacağı açıkça belirlenir; relay ile çift throttle veya kota/limit atlatma olmamalıdır.
- Tekrar kullanılabilir ve düşük ek bellek kullanan, **streaming** throttle tasarımı: içerik RAM'e alınmamalı, büyük .bak dosyası diskten tekrar kopyalanmamalı.
- Limitin yalnız **upload transfer byte'ları** için uygulanması; DB backup, checksum, heartbeat, lease yenileme, admin API ve kontrol mesajları etkilenmemeli.
- Cancellation ve agent/backend restart durumlarında permit/stream/lease kaynakları kapanmalı; transferin doğruluğu ve durably tracked partial nesne garantileri korunmalı.
- İleride başka backup engine ve storage provider ile yeniden kullanılabilir olmalı; yeni provider zorunlu interface metotları olmadan desteklenmeli.
- Default davranış mevcut sınırsız aktarımın geriye dönük karşılığı olmalı; tenant izolasyonu ve admin yetkileri korunmalı.

**Faz 2A kabul senaryoları:** Kontrollü saat/byte ölçümü ile düşük/orta/büyük stream'de üst hız sınırı; iki paralel upload'ın aynı tenant toplam sınırını aşmaması; farklı tenant'ların birbirinden bağımsız davranması; job override precedence; sınırsız mod regresyonu; cancel/retry/restart/lease kaybı; SFTP + backend relay yolları; aynı içerik için checksum ve boyut doğrulamasının değişmemesi. Başlangıçta deterministik test, ardından izole ağ smoke kullanılır.

## Faz 2B — Saat bazlı transfer profilleri (**Planned**)

- Tenant policy ve job override altında zaman dilimine göre bant genişliği profilleri.
- Schedule time zone, geceye taşan pencereler ve DST değişimlerini açık tanımlama.
- Devam eden uzun aktarımda profil geçişinin güvenli biçimde uygulanması.

Örnek politika (ürün tercihi, hardcode varsayılan değil):

```text
08:00-18:00 → 10 Mbps
18:00-08:00 → Unlimited
```

## Faz 2C — Upload pause/resume, checkpoint ve network recovery (**Planned**)

- Operatör pause/resume durumu ile cancellation ayrı; yeni durumların DB'de kalıcı olması gereken kapsam önceden belirlenecek.
- Provider `Resume`/`Multipart` yetenekleri ve gerçekten desteklediği mekanizmalar üzerinden checkpoint; desteklemeyen provider'larda güvenli yeniden deneme.
- Ağ kopması ve process restart sonrasında partial upload, orphan/quarantine, quota, lease fencing ve idempotency kurallarının korunması.
- Restore/download akışlarının pause/resume kapsamında olup olmadığı ayrıca ürün kararıdır; başlangıç Faz 2A yalnız upload içindir.

**Mimari kural:** Transfer policy backup engine'den ve storage provider implementasyonundan ayrıdır. Faz 2A bitmeden Faz 2B/2C tamamlanmış sayılmaz; her dilim kendi test ve kabul koşullarıyla kapanır.

---

# 5. Faz 3 — File/Folder Backup

**Durum: Planned.** `BackupProviders.FileSystem` projesi repo'da iskelet olarak var; gerçek file enumeration, manifest, backup/restore pipeline henüz yok. FileSystem ayrı backup provider olacaktır.

## İlk implementasyon

- Dosya seçimi.
- Klasör seçimi.
- Recursive backup.
- Include/exclude pattern.
- Hidden/system file politikaları.
- Symlink/reparse point davranışı.
- Compression.
- Source checksum/manifest.
- Backup artifact.
- Restore metadata.
- Partial failure handling.

## Locked/open file davranışı

İlk implementasyonda snapshot yoksa:

- locked dosya raporlanmalı,
- sessizce atlanmamalı,
- backup sonucu `warning/partial` gibi ayırt edilebilir olmalıdır.

---

# 6. Faz 4 — Windows VSS

FileSystem provider'ın consistency seviyesini artırmak için Windows snapshot provider eklenir.

Abstraction:

```text
ISnapshotProvider
```

Windows implementation:

```text
WindowsVssSnapshotProvider
```

## Yapılacaklar

- VSS snapshot create.
- Snapshot lifecycle.
- Snapshot path mapping.
- Backup sonrası cleanup.
- Failure cleanup.
- Timeout.
- Disk space kontrolü.
- VSS writer state kontrolü.
- Snapshot capability reporting.

FileSystem backup doğrudan VSS implementation'a bağımlı olmamalıdır.

---

# 7. Faz 5 — Linux Agent

**Durum: Partial.** Linux platform host, agent çalıştırma ve owner-only enrollment mevcut. **systemd unit, install/uninstall otomasyonu ve servis paketleme** eksik; aşağıdaki özelliklerin tamamı yapılmamış sayılmamalıdır. `Agent.Core` üzerinden Linux desteği genişletilir.

## Linux host

- systemd service.
- install/uninstall.
- configuration.
- secure local agent credential storage.
- filesystem abstraction.
- process execution abstraction.
- resource information.
- heartbeat.
- push/poll communication.
- capability discovery.

## Uyumluluk

Aynı backend:

```text
Windows Agent
Linux Agent
```

ile konuşmalıdır.

OS bilgisi Agent metadata'sının parçası olacaktır.

Backend path normalization yapmaya çalışmayacaktır.

Kaynak path:

```text
OperatingSystem
Path
```

şeklinde anlamlandırılacaktır.

---

# 8. Faz 6 — Linux Snapshot Providers

Linux FileSystem backup için snapshot provider'lar ayrı modüller halinde eklenir.

Sıralama kullanım ihtiyacına göre yapılabilir:

- LVM,
- Btrfs,
- ZFS.

Her provider:

- capability discovery,
- create snapshot,
- mount/access snapshot,
- cleanup,
- free-space checks,
- error mapping

sağlamalıdır.

Snapshot provider bulunmayan sistemlerde normal filesystem backup çalışmaya devam edebilmelidir.

---

# 9. Faz 7 — Docker Runtime Desteği

Database engine ile runtime birbirinden bağımsız olacaktır.

```text
IDatabaseBackupProvider
IDatabaseRuntime
```

Runtime modülleri:

```text
NativeDatabaseRuntime
DockerDatabaseRuntime
```

## Discovery

İki yöntem desteklenecektir:

- otomatik discovery,
- manuel tanımlama.

## Güvenlik

Agent ana process'ine doğrudan geniş Docker socket yetkisi verilmemelidir.

Ayrı privileged helper kullanılacaktır.

Örnek logical yapı:

```text
BackupAgent
    ↓ restricted local IPC
Docker Runtime Helper
    ↓
Docker Engine
```

Helper:

- sadece gerekli Docker discovery/runtime işlemlerini expose etmeli,
- generic arbitrary command execution sağlamamalı,
- allowlist tabanlı dar API kullanmalı,
- agent identity doğrulamalı,
- audit edilebilir olmalıdır.

## Database bağlantısı

Container MSSQL/PostgreSQL backup işlemleri mümkün olduğunca normal database network protocol'ü üzerinden yürütülmelidir.

Docker runtime sadece:

- discovery,
- container metadata,
- port mapping,
- lifecycle/context

sağlamalıdır.

Temel backup yolu `docker exec` bağımlı olmamalıdır.

---

# 10. Faz 8 — MSSQL Restore

**Durum: Completed (MSSQL Full Restore).** `RESTORE VERIFYONLY` altyapısı gerçek Full Restore'a, API/Agent/Restore Wizard akışına genişletildi. Aşağıdaki maddeler tarihsel gereksinimleri anlatır. Multi-set, differential/log chain, PITR ve özel dosya yerleşimleri kapsam dışıdır.

## Restore pre-check

- Backup metadata.
- Source SQL version.
- Target SQL version.
- Edition.
- Compatibility level.
- Backup type.
- Encryption.
- TDE.
- Disk space.
- Target database state.

## Metadata

Kullanılacak SQL metadata mekanizmaları:

- `RESTORE HEADERONLY`
- `RESTORE FILELISTONLY`

## İlk restore özellikleri

- yeni database adına restore,
- alternate file path,
- `WITH MOVE`.

## Sonraki adım

- existing database overwrite,
- cross-instance restore,
- confirmation flow,
- active connection handling,
- rollback/recovery behavior.

Production üzerine restore kritik işlem sayılmalı ve explicit confirmation gerektirmelidir.

---

# 11. Faz 9 — File/Folder Restore

## Özellikler

- full folder restore.
- alternate path restore.
- overwrite existing.
- skip existing.
- restore manifest validation.
- checksum doğrulama.
- permissions/metadata policy.

OS uyumluluğu ayrıca ele alınmalıdır.

Örneğin Windows ACL metadata'sının Linux hedefe uygulanması otomatik varsayılmamalıdır.

---

# 12. Faz 10 — MSSQL Differential Backup

MSSQL provider genişletilir.

## Gereksinimler

- Full backup dependency.
- Differential base tracking.
- Backup metadata.
- Restore chain validation.
- Capability detection.
- Job policy update.
- UI selection.

Mevcut Full Backup yolu değiştirilmemelidir.

Differential ayrı provider capability olarak eklenmelidir.

---

# 13. Faz 11 — MSSQL Transaction Log Backup

## Gereksinimler

- Recovery model doğrulama.
- FULL/BULK_LOGGED desteği.
- Log chain metadata.
- LSN metadata.
- Backup chain validation.
- Log backup scheduling.
- Chain-break detection.

Metadata:

```text
BackupStartLSN
BackupFinishLSN
DatabaseBackupLSN
FirstLSN
LastLSN
CheckpointLSN
```

---

# 14. Faz 12 — Point-in-Time Restore

Full + Differential + Log zincirleri oturduktan sonra eklenmelidir.

## Yapılacaklar

- Restore plan builder.
- Chain validation.
- Required artifact calculation.
- `NORECOVERY` / `RECOVERY` orchestration.
- Target timestamp seçimi.
- Missing chain detection.
- Restore simulation/preview.
- Restore audit.

Bu özellik Transaction Log backup tamamlanmadan yapılmamalıdır.

---

# 15. Faz 13 — PostgreSQL Provider

PostgreSQL ayrı `IDatabaseBackupProvider` implementation olacaktır.

MSSQL kodu reuse edilmeye çalışılmamalıdır; ortak olan orchestration/contracts katmanı reuse edilmelidir.

## İlk PostgreSQL iterasyonu

Logical backup:

- database discovery,
- server version discovery,
- database metadata,
- `pg_dump`,
- compression,
- checksum,
- artifact generation,
- storage,
- restore verification yaklaşımı.

Native ve Docker runtime aynı provider ile çalışabilmelidir.

## Sonraki PostgreSQL iterasyonu

Physical backup:

- base backup,
- WAL,
- retention-aware backup chain,
- PITR,
- restore planning.

İlk PostgreSQL sürümünde physical backup/WAL zorunlu değildir.

---

# 16. Faz 14 — Multi-Storage Replication

MVP'de hazır bulunan:

```text
BackupArtifact
    ↓
BackupReplica
```

modeli aktif kullanılmaya başlanır.

## Özellikler

- tek artifact → birden fazla storage.
- required replica count.
- primary/secondary storage.
- replica health.
- per-replica retry.
- replica delete policy.
- partial success.
- replication repair.

Örnek:

```text
Artifact
├── Local Backup Server
├── NAS
└── S3
```

> **Durum: Completed (temel Faz 14).** Canonical `destinations`, `isPrimary`, `requiredVerifiedReplicaCount`, verified replica status, per-destination outcomes, repair ve durable quarantine aktif. Yukarıdaki NAS/S3 örneği hedef mimaridir; provider'lar henüz yoktur. Tam **3-2-1 storage class/off-site** zorunluluğu Faz 15'te ayrı Planned. Replica'ların quota'ya sayılması Bölüm 2A.4/2A.11'e göre policy'yle belirlenir.

---

# 17. Faz 15 — 3-2-1 Policy Engine

**Durum: Partial.** Required verified replica count ve Healthy/Degraded/InsufficientReplicas projeksiyonu var. Ancak aşağıdaki **depolama sınıfı** ve **off-site** kurallarını yöneten tam 3-2-1 policy engine henüz bulunmuyor. Multi-storage üzerine bu ileri politika katmanı eklenir.

Sistem policy ihlallerini algılamalıdır.

Örnek:

```text
Required Copies: 3
Required Storage Classes: 2
Required Off-site Copies: 1
```

Dashboard:

- compliant,
- degraded,
- non-compliant

durumu gösterebilir.

---

# 18. Faz 16 — S3 ve Object Storage

Storage provider abstraction kullanılarak:

- S3 compatible,
- AWS S3,
- MinIO benzeri S3-compatible endpointler

eklenebilir.

## Capability'ler

- multipart upload,
- resume,
- server-side checksum,
- versioning,
- Object Lock,
- retention lock.

Generic storage interface Object Lock özellikleriyle kirletilmemelidir.

Capability/optional interface üzerinden uygulanmalıdır.

---

# 19. Faz 17 — Immutable Backup / Object Lock

Ransomware korumasının önemli aşamasıdır.

## Özellikler

- immutable replica.
- WORM retention.
- Object Lock.
- delete protection.
- policy lock.
- audit.

Agent mümkün olduğunca:

- upload

yetkisine sahip olup:

- retention değiştirme,
- eski artifact silme,
- policy kaldırma

gibi yetkilere sahip olmamalıdır.

---

# 20. Faz 18 — NAS / SMB

## Özellikler

- SMB storage provider.
- credentials.
- domain account.
- share availability.
- permissions pre-check.
- free space query.
- reconnect.
- network interruption handling.

Windows ve Linux agent senaryoları ayrı test edilmelidir.

---

# 21. Faz 19 — Google Drive / Cloud Drive Provider

**Durum: Completed (Google Drive temel provider); Acceptance Pending.** Tenant-owned OAuth bağlantısı, güvenli token yönetimi, staging ve resumable upload implementasyonu mevcut. Canlı Google Drive API hesabıyla uçtan uca kabul henüz çalıştırılmadı; provider-reported kapasite izleme Faz 21'e kalır.

## Yapılacaklar

- tenant-owned connection.
- OAuth token lifecycle.
- refresh token secure storage.
- folder selection.
- resumable upload.
- quota monitoring.
- token revoke.
- provider capability mapping.

Cloud token'ları agent'a persistent verilmemelidir.

> Tenant-owned Google Drive connection'ı Bölüm 2A.10'daki Storage Connection modelini kullanır. İstenirse quota kapsamına alınabilir (2A.4). Provider-reported quota izleme bu faza aittir; tenant ticari quota'sı Bölüm 2A'dadır.

---

# 22. Faz 20 — Restore Test Automation

**Durum: Planned — küçük `R1` dilimi, yüksek öncelik.** Genel teknik sıra Faz 20 olarak kalır, fakat kullanıcı açısından güvenilir geri yükleme, yeni backup engine/provider sayısını artırmaktan önce gelir. Backup'ın var olması başarılı restore anlamına gelmez.

**R1 (ilk teslim):** Kullanıcının açıkça seçtiği/sahip olduğu artifact için disposable/izole test SQL ortamında Full restore + veri bütünlüğü/DB açılış kontrolü; güvenli cleanup, test sonucu ve audit. Otomatik zamanlayıcı ikinci küçük dilimde devreye alınır. Üretim veritabanına test restore yapılmaz. **R1 kabulü:** başarılı/yetersiz-kapasite/bozuk-artifact/SQL failure/cleanup failure; retry ve restart; bağımsız tenant; sonuç ekranında gerçek `Restored/Failed/NotRun` durumu.

## Özellikler

- scheduled restore test.
- isolated target.
- MSSQL restore test.
- integrity validation.
- cleanup.
- result history.
- failure notification.

Backup health değerlendirmesinin temel input'larından biri olacaktır.

---

# 23. Faz 21 — Storage Capacity Monitoring

Provider destekliyorsa:

- total capacity,
- used,
- free,
- quota

bilgisi izlenir.

Capability:

```text
SupportsCapacityQuery
```

Threshold:

- warning,
- critical.

Tenant-specific threshold desteği eklenebilir.

> **Not:** Bu faz, provider tarafından raporlanan kapasiteyi (disk/hesap doluluğu) izler. Tenant'ın ticari quota'sı (Bölüm 2A) platformun kaydettiği replica boyutlarına dayanır ve provider'ın `SupportsCapacityQuery` desteklemesini gerektirmez. İkisi birbirinin yerine kullanılmaz.

---

# 24. Faz 22 — Backup Health

Health doğrudan tek bir sayıya indirgenmek zorunda değildir.

Önce health state üretilebilir.

Input örnekleri:

- last successful backup.
- backup age.
- verification status.
- replica count.
- restore test.
- storage availability.
- retention compliance.
- 3-2-1 compliance.

Skor eklenirse kriterleri ve ağırlıkları açıkça tanımlanmalıdır.

---

# 25. Faz 23 — Backup Size Anomaly Detection

Basit istatistiksel detection ile başlanabilir.

Örnek:

```text
Normal: 20-22 GB
Current: 4 GB
```

Sistem:

- backup'ı otomatik başarısız saymamalı,
- anomaly event oluşturmalı,
- kullanıcıya görünür hale getirmelidir.

Daha sonra gelişmiş modeller eklenebilir.

---

# 26. Faz 24 — Bildirim Provider'ları

**Durum: Planned — `N1` erken ürün dilimi.** 2026-10-13 itibarıyla repo'da bağımsız çalışan notification provider/worker modülü yok; tarihsel MVP tasarımında söz edilen internal notification modülü silinmiş olabilir. Önce mevcut domain olaylarını ve durable transaction/outbox imkanını incele; olmayan modülü var sayma.

**N1 (ilk teslim):** başarısız veya uzun süredir başarılı olamayan backup, offline agent, kritik kota ve replica yetersizliği olaylarını **email ve/veya generic HTTPS webhook** üzerinden tenant ayarına göre ulaştırma; delivery history, retry/backoff, suppression/dedup ve unsubscribe/disable. Güvenlik için webhook URL izinleri, SSRF/DNS/private-IP önlemleri, secret redaction ve tenant izolasyonu zorunludur. Teams/Slack/Telegram adaptörleri yalnız ihtiyaç olduğunda eklenir. **N1 kabulü:** başarısız gönderim, aynı olayın tekrar gelmesi, tenant/policy değişimi, burst rate limit, kapalı provider, restart ve sistemin backup akışını bildirim arızasında durdurmaması.

İlk tasarım, MVP'nin eksik görünen `NotificationEvent` altyapısının gerçekte hangi parçasının bulunduğunu ayrı doğrular; yoksa sınırları dar yeni modül kurar.

Sıra:

1. Email
2. Teams / Slack webhook
3. Telegram

## Tasarım

```text
NotificationEvent
    ↓
NotificationPolicy
    ↓
NotificationProvider
```

Backup domain provider'lara doğrudan bağımlı olmamalıdır.

Destek:

- tenant policy,
- severity,
- event type,
- rate limiting,
- duplicate suppression.

---

# 26A. E1 — Storage'a Gönderilmeden Önce Artifact Şifreleme (**Planned, yüksek güvenlik önceliği**)

**Bu yeni bir çapraz fazdır; Faz 1–30 numaraları korunur.** Provider-side HTTPS/SFTP veya sağlayıcının server-side encryption'ı, müşteri veritabanının üçüncü taraf saklama ortamında uçtan uca gizliliği anlamına gelmez. Özellikle SFTP, Google Drive, gelecekte S3/SMB için isteğe bağlı client-side artifact encryption politikası tasarlanacaktır.

**E1 tasarım ve kabul sınırı:**

- Şifreleme yeri/anahtar sahibi (Agent veya backend), backend-relayed transfer, backup/restore/download/checksum/replication/retention etkileri için tehdit modeli çıkar. Kaynağın plaintext checksum'ı ile **şifreli artifact checksum'ı** ayrı tanımlanır.
- **Authenticated streaming encryption (AEAD)** ve versiyonlu envelope metadata; large .bak için bounded-memory; key id/nonce doğruluğu; anahtar rotasyonu ve felaket kurtarma prosedürü. Key kaybolursa restore edilemeyeceği açıkça test edilir.
- Anahtar/secret hiçbir zaman log, audit, frontend veya kalıcı agent görevi içinde plaintext görünmez. Yetki kontrolleri tenant sınırlarını aşamaz. Mevcut artifacts geriye dönük sessizce değiştirilmez: `unencrypted v1` ve `encrypted v2` birlikte okunabilir olmalı.
- Başarısız/yarım upload, retry, lease takeover, target conflict/quarantine, çeşitli storage provider'lar ve cross-agent restore senaryolarıyla gerçek checksum korunur.
- İlk PR **sadece mimari/karar + deterministic test yaklaşımı**, ikinci dilim seçilen storage akışında tam vertical slice, üçüncü dilim provider/restore uyumluluğu. **Karmaşıklık gerekçesiz zorunlu şifrelemeye geçilmez** ve eski backup erişilebilirliği korunur.

---

# 27. Faz 25 — Agent Auto-Update

MVP'de agent/protocol versioning mevcut olmalıdır.

Auto-update bunun üzerine kurulur.

## Özellikler

- release metadata.
- signed packages.
- update eligibility.
- staged rollout.
- tenant policy.
- rollback.
- failed update recovery.
- minimum supported version.
- forced security update.

Windows ve Linux package/update mekanizmaları platform-specific modüllerde tutulmalıdır.

---

# 28. Faz 26 — mTLS / Certificate Agent Authentication

İlk sürüm agent secret modelinden daha güçlü modele geçiş seçeneği.

## Özellikler

- client certificate enrollment.
- certificate rotation.
- revocation.
- expiry monitoring.
- mTLS.
- migration from shared/unique secret.

Contracts ve agent identity modeli değişmeden kalmalıdır.

---

# 29. Faz 27 — External Secret Managers

Mevcut `SecretProvider` abstraction üzerine:

- HashiCorp Vault,
- Azure Key Vault,
- diğer environment-specific provider'lar

eklenebilir.

Tenant secrets ile platform secrets ayrılmalıdır.

Secret rotation mümkün olduğunca provider üzerinden desteklenmelidir.

---

# 30. Faz 28 — Advanced Audit / Tamper Resistance

MVP'de audit append-only olacaktır.

İleri aşamada:

- external audit sink,
- SIEM integration,
- immutable audit storage,
- signed audit batches,
- retention rules

eklenebilir.

---

# 31. Faz 29 — Gelişmiş Raporlama

## Raporlar

- backup success rate.
- failed jobs.
- storage growth.
- backup duration trend.
- backup size trend.
- agent availability.
- restore test results.
- tenant usage.
- retention cleanup.
- replica health.

Raporlama transactional domain tablolarını aşırı yüklememelidir.

Gerekirse projection/read model kullanılabilir.

---

# 32. Faz 30 — Operasyonel Ölçeklenme

Sistem büyüdüğünde:

- multiple backend instances,
- Quartz clustering,
- distributed locking,
- queue separation,
- worker scaling,
- large tenant isolation,
- rate limiting,
- connection scaling

ele alınabilir.

Bu ihtiyaç oluşmadan mikroservis bölünmesi yapılmamalıdır.

Modüler monolith sınırları doğru kurulmuşsa daha sonra gerektiğinde servis extraction yapılabilir.

---

# 33. Modülerlik Kuralları

Uzun vadede teknik borcun kontrol altında kalması için:

1. Domain hiçbir storage/database/runtime implementation bilmemelidir.
2. MSSQL modülü PostgreSQL modülünü bilmemelidir.
3. Storage provider'lar birbirlerini bilmemelidir.
4. Windows platform modülü Linux platform modülünü bilmemelidir.
5. Agent.Core OS-specific API kullanmamalıdır.
6. Database provider runtime-specific Docker API kullanmamalıdır.
7. Docker runtime database-specific backup logic içermemelidir.
8. Notification provider backup orchestration'a gömülmemelidir.
9. Secret provider doğrudan business logic'e yayılmamalıdır.
10. Contracts versioned olmalıdır.
11. EF entity'leri wire protocol modeli olarak kullanılmamalıdır.
12. Provider support durumu capability sistemi üzerinden açıklanmalıdır.
13. Ticari katman (plan, quota, usage, support access) backup orchestration, storage provider ve agent koduna gömülmemelidir; yalnız dar bir admission arayüzüyle haberleşir.
14. Storage provider quota, plan veya billing kavramını bilmemelidir.
15. Quota kapsamı ownership veya provider tipine hardcode edilmemeli; `QuotaPolicy` verisiyle belirlenmelidir.
16. Retention, quota baskısıyla sessizce override edilmemelidir.
17. PlatformAdmin rolü tek başına müşteri backup verisine erişim yetkisi değildir; erişim yalnız aktif, audit edilen `SupportAccessSession` ile olur.
18. Storage connection credential'ları plaintext saklanmamalı ve frontend'e geri dönmemelidir.

---

# 34. Gelecekte Desteklenebilecek Kombinasyonlar

Mimari aşağıdaki kombinasyonların doğal olarak oluşmasına izin vermelidir:

```text
Windows + Native MSSQL
Windows + Docker MSSQL

Linux + Native MSSQL
Linux + Docker MSSQL

Linux + Native PostgreSQL
Linux + Docker PostgreSQL

Windows + FileSystem
Windows + VSS + FileSystem

Linux + FileSystem
Linux + LVM + FileSystem
Linux + Btrfs + FileSystem
Linux + ZFS + FileSystem
```

Yeni kombinasyon eklemek core backup orchestration'ın yeniden yazılmasını gerektirmemelidir.

---

# 35A. Güncel Üst Seviye Öncelik Sırası — özet

**2026-10-13 ürün kararı:** Geliştirme devam ediyor; staging/production dağıtım işi yok. Yüz yüze satış ve PlatformAdmin üzerinden manuel plan atama yeterli; online billing `M9` kapsam dışıdır. **Tek aktif öncelik listesi Bölüm 35'tir.** Bu bölüm yalnız bağımlılık/başlık özetidir.

```text
MVP MSSQL Full + Restore / Managed Service / Multi-Destination    [Completed, temel]
        ↓
A0 — MVP operasyon audit matrisi ve eksik kayıtlar                [Completed: çalışma dalı]
        ↓
Faz 2A — Upload bandwidth limiting                               [NEXT: yeni ürün özelliği]
        ↓
N1 (Faz 24) — Email/webhook bildirimleri + R1 (Faz 20) restore testi  [Öne çekildi]
        ↓
E1 — Artifact şifreleme mimarisi / ilk güvenli vertical slice      [Öne çekildi]
        ↓
MSSQL Differential → Transaction Log → PITR; File/Folder Backup   [Planlı, karar kapısı]
        ↓
Gerektiğinde Linux systemd, Docker, VSS, PostgreSQL, S3/SMB        [Bağımlılık/ürün ihtiyacına göre]
```

Faz 2B–2C, 3-2-1, provider genişletmeleri ve diğer fazlar kapsamdan çıkarılmadı; ancak **Faz 2B/2C'nin mutlaka yeni kullanıcı güvenilirliği özelliklerinden önce tamamlanması gerekmiyor**. Her dilimin kapsamı, bağımlılığı ve kendi kabul ölçütleriyle karar verilecek.

---

# 35. Tek Aktif İş Sırası ve Kabul Kapıları

Aşağıda **öncelik sırası** verilir; eski `Faz` numaraları ve `M` dilimleri *iş kimliğidir*, kronolojik teslim sırası değildir. Kodda yapılmış işler yeniden açılmaz. Tek geliştirici temposunda 1–2 PR'lık küçük dikey dilimler hedeflenir; eforun tahmini uygulama öncesi yapılır.

| Öncelik | İş kimliği ve kapsam | Kabul / bağımlılık | Durum |
|---|---|---|---|
| 0 | **A0 — Kritik audit matrisi ve eksik event'ler** | MVP madde 33; veri/actor/tenant güvenliği, atomik yazım, DB testleri; Bölüm 1B kanıt/sınırlar | **Completed — çalışma dalı, 2026-10-14** |
| 1 | **Faz 2A — Upload bandwidth limiting** | Per-agent/tenant/job, bounded stream, paralel transfer ölçümü, relay/SFTP regresyon | **Next: yeni ürün özelliği** |
| 2 | **N1 (Faz 24) — Email + generic webhook alarm** | Durable event/dispatch, dedup, retry, SSRF koruması, tenant policy, CI | Planned — öncelikli |
| 3 | **R1 (Faz 20) — İzole MSSQL restore testi** | Disposable SQL, tutarlı veri doğrulama, fail/cleanup/retry, tenant izolasyonu | Planned — öncelikli |
| 4 | **E1 — Artifact encryption** | Önce tehdit modeli/anahtar yaşam döngüsü, sonra streaming encrypt/decrypt + restore | Planned — güvenlik |
| 5 | **Faz 10–12 — MSSQL Differential / Transaction Log / PITR** | Full/log base chain, LSN, restore ordering, retention-safe pin, MSSQL E2E | Planned — MSSQL ürün değeri |
| 6 | **Faz 3 ve 9 — File/Folder Backup + Restore** | Include/exclude, symlink, partial warnings, manifest, güvenli restore; VSS sonra | Planned — ikinci kaynak |
| 7 | **Faz 2B/2C — Transfer profilleri ve recovery** | Profil/DST, pause/resume, provider gerçek capability ve durable checkpoint | Planned — ihtiyaçla öne alınabilir |
| 8 | **Faz 15/16/17/18 — 3-2-1, S3, Object Lock, SMB/NAS** | Provider-specific safety, encryption/key policy, immutable retention, failure acceptance | Planned |
| 9 | **Faz 4–7/13/21–23/25–30** | VSS/Linux installer/snapshot, Docker, PostgreSQL, monitoring/health/anomaly, updates, advanced security/reports | Future / ihtiyaç bazlı |
| Ayrı | **A1 — Canlı provider & Windows acceptance** | Gerçek Drive OAuth/API ve Windows DPAPI/MSSQL VM `NOT RUN` kapanışı | Acceptance Pending; deployment değil |
| Ayrı | **O1 — Veri yaşam döngüsü / platform DR / uyumluluk / observability** | Test ortamları, saklama ve silme politikası, DB restore tatbikatı, metrics | Planned — çapraz |
| Kapsam dışı | **M9 — Online billing / checkout / ödeme provider'ı** | Yüz yüze/doğrudan satış kararı | Not planned |

## Ortak Definition of Done (tüm yeni dilimler)

1. **İş sınırı:** Uygulanan capability, desteklenmeyen ortam/provider, devre dışı durum ve geriye uyumluluk net yazılmış olmalı.
2. **Güvenlik:** Actor/tenant isolation, secrets, rate/size limits, bütünlük, geri alınamaz işlemler, lease/owner fencing değerlendirilmeli; gerekiyorsa threat/failure matrix eklenmeli.
3. **Dayanıklılık:** Cancel, timeout, partial transfer, retry, DB/agent restart, stale operation ve yarış senaryoları uygun kapsama göre deterministik test edilmeli.
4. **Kanıt:** Unit, architecture, DB integration ve provider/VM acceptance matrisi; çalışmayan test **NOT RUN** ve gerekçesiyle raporlanmalı. CI PASS canlı kabul iddiası değildir.
5. **Geriye uyumluluk:** Migration uygulanmış/apply edilmemiş schema, eski agent, eski artifact ve restore/download senaryoları korunmalı veya açık uyumluluk engeli yazılmalı.
6. **Teslim:** Odaklı commit/PR ve güncel durum notu; ayrı MVP/roadmap tabloları tutarsız bırakılmamalı. Kullanıcı onayı olmadan deployment/release/billing entegrasyonuna geçilmemeli.

Temel kural:

> Önce güvenle geri yüklenebilen, gözlemlenebilen MSSQL backup ürününü geliştir. Çok sayıda provider/özellik için temel backup yolunu veya veri güvenliğini karmaşıklaştırma.
