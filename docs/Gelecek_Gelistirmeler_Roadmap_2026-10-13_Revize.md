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
| Local/Central/Managed/SFTP/Google Drive storage | Completed (temel kapsam) | Central managed connection olarak kullanılır; canlı Google Drive kabulü A1'de geçti (Linux ve Windows Agent; 122 MB upload bug'ı düzeltildi), multi-destination canlı koşusu ve Windows Agent'tan SFTP `NOT RUN`. |
| Multi-destination, replica health, repair ve quota | Completed (temel kapsam) | Canonical destinations, atomic repair quota, lease-fenced cleanup, durable quarantine. Tam 3-2-1 sınıf/lokasyon policy engine ayrı faz. |
| Retention, soft delete, verified physical cleanup | Completed | Orphan/partial cleanup ve korumalı replica/restore pin akışları. |
| Backup Browser/Download, Storage/Quota UI | Completed | Owner/Admin download, doğrulama, tenant ve PlatformAdmin yönetimi. |
| Plans, Subscriptions, Support Access/Support Restore | Completed (tanımlı kapsam) | Support session, audit, kontrollü müşteri ortamına restore; izole support SQL ortamı Future. |
| Accessible dashboard ve restore/backup wizard | Completed | PR #5 `main` içine birleştirildi; success KPI kapsamı ve warning kategori etiketleri düzeltildi. |
| Kritik olay audit'i | **Completed — A0 / A0.1, çalışma dalı** | 2026-10-14: olay/servis/transaction/actor/tenant/test matrisi Bölüm 1B'de; `backup_storage_cleanups` durable orphan cleanup ledger ile crash-safe audit; `feat/a0-audit-trail-completion`. |
| Bildirimler (email, webhook, SMS) | **Completed — N1 (email + webhook); SMS `IMPLEMENTED — NOT VERIFIED`, çalışma dalı `feature/n1-notifications`** | 2026-10-14: durable event/outbox + lease'li delivery worker, tenant policy/kanal/alıcı API + UI, SMTP/HTTPS webhook/generic HTTP SMS adaptörü; kayıt ve kanıt Bölüm 26'da. |
| CI ve güvenlik hardening | Completed (otomatik kontroller) | `main` Fast CI başarılı; bu, gerçek Windows/Google Drive ve tüm dış servis kabul testlerinin yerine geçmez. |

## Henüz tamamlanmamış ana alanlar

**A0 audit kapanışı, Faz 2A (upload bandwidth limiting) ve N1 bildirimleri (email + webhook + SMS) tamamlandı** (Bölüm 1B, Bölüm 4, Bölüm 26). N1'de **SMS `IMPLEMENTED — NOT VERIFIED`**'dır (hiçbir gerçek SMS sağlayıcısına karşı çalıştırılmadı; SMS'e özel test bilinçli olarak yazılmadı). **Sıradaki:** R1 izole restore testi ve E1 artifact şifreleme (Bölüm 35); Faz 2B/2C bu iterasyonda başlanmadı.

**Planlı:** Faz 2B zaman profilleri ve 2C pause/resume/recovery; **erken öncelikli** izole MSSQL restore-test otomasyonu ve backup artifact şifreleme; MSSQL Differential/Log/PITR; File/Folder Backup ve File Restore; Windows VSS; Linux systemd installer; Docker runtime/discovery; PostgreSQL backup; S3/SMB/FTPS; provider kapasite izleme; tam 3-2-1 ve ileri audit; Teams/Slack/Telegram/push bildirimleri. Ayrıntılı **teslim sırası** Bölüm 35'tedir; faz numarası öncelik demek değildir.

**Kabul durumu:** Gerçek Google Drive OAuth/API ve Windows Agent/MSSQL kabulü 2026-10-14'te A1 ile çalıştırıldı (Bölüm 1B, senaryo matrisi); kapsam dışı kalanlar orada `NOT RUN` olarak listelidir. 2026-10-14'te ayrıca açık Win11 VM'de runtime-only DPAPI probe smoke geçmişti. VM'de .NET 10.0.12 ve SQL Server çalışıyor; SDK yok. İzole A1 smoke Agent'ı kaldırıldı; dev tenant'a bağlı `dev-windows-agent` bilerek kurulu bırakıldı (`scripts/windows/dev-windows-agent.sh uninstall`).

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

## A1 — Canlı Google Drive & Windows 11 acceptance (**Completed — 2026-10-14, çalışma dalı `test/a1-live-provider-acceptance`; kapsam dışı kalanlar `NOT RUN`**)

Gerçek Google hesabı (yalnız `drive.file` scope; uygulamanın oluşturduğu `Backup Platform/<tenant>/…` klasörü) ve gerçek Windows 11 VM'de (libvirt `win11`, .NET runtime 10.0.12, SQL Server 17.0.1000.7 named instance, SDK yok; self-contained `win-x64` Agent publish) çalıştırıldı. SQL işlemleri yalnız `bp_a1_win_*` / `bp_google_drive_acceptance*` test veritabanlarında yapıldı; credential içerikleri çıktıya/log'a/Git'e yazılmadı. Mevcut araçlar yeniden kullanıldı: `scripts/dev/acceptance.py` (+ `retry` adımı), `scripts/frontend/run-download-smoke.sh` (gerçek Windows service enrollment + `master` backup + download), yeni `scripts/windows/run-a1-acceptance.sh` / `a1-windows-acceptance.py` (yaşam döngüsü senaryoları).

| # | Senaryo | Sonuç | Kanıt / not |
|---|---|---|---|
| D1 | Taze OAuth (kullanıcı tarayıcıda, Librewolf dışı): bağlantı oluştur + izin | **PASS** | Yalnız `refreshToken` secret alanı kaydedildi, `reauthorizationRequired=false`, bağlantı testi `Succeeded` (canlı Drive API çağrısı). |
| D2 | Token refresh | **PASS (dolaylı)** | 9 Ekim'de alınmış refresh token API yeniden başlatıldıktan sonra (access-token cache boş) başarıyla kullanıldı; yeni bağlantı da API restart sonrası yüklemeyi sürdürdü. Refresh'e özel log/telemetri yok. |
| D3 | Upload + ReadBack doğrulama + SHA-256 (577.536 B) | **PASS** | Replica `Verified/ReadBack`, artifact checksum = replica checksum; eski ve yeni bağlantıda. |
| D4 | 122 MB upload (>Kestrel varsayılan 28,6 MB limiti) | **FAIL → düzeltildi → PASS** | Bug 1 (aşağıda). Düzeltme sonrası yükleme, indirme SHA-256 eşleşmesi, silme PASS (eski + yeni bağlantı). |
| D5 | Backend-authorized download + SHA-256/uzunluk | **PASS** | 577 KB ve 122 MB; lifecycle `Completed`, aktarılan bayt tam. |
| D6 | Drive replica'sından restore (NewDatabase, precheck + içerik checksum eşitliği) | **PASS** | Yeni bağlantıdaki ilk koşu leftover `_restored` DB'ye çarptı: ürün `TargetDatabaseExists` ile doğru reddetti (araç eksikliği, düzeltildi); temiz koşuda kaynak = restore (`ONLINE 5 -1308801917`). |
| D7 | Kesintili yükleme + otomatik retry | **PASS** | Yükleme sürerken API süreci öldürülüp yeniden başlatıldı: attempt 1 `Failed/StorageUnavailable`, orphan artifact `Deleted`, attempt 2 `AutomaticRetry` `Succeeded`, tek `Verified` replica, indirme SHA-256 eşleşti (eski ve yeni bağlantıda). Retry politikası tükenince manuel retry `retry_not_allowed/attempts_exhausted` döndü (tasarım gereği). |
| D8 | Cleanup: delete → retention worker → absence | **PASS** | Artifact `Deleted`, replica doğrulanmış yokluk sonrası `Deleted`, kota geri indi. |
| D9 | Disconnect / token revoke | **PASS** | `hasSecret=false`, `reauthorizationRequired=true`, sonraki bağlantı testi fail-closed (`secret_unavailable`). Google tarafında iptal de gözlendi: aynı hesaptaki diğer bağlantı da `authentication_failed` / `reauthorizationRequired=true` oldu (aşağıdaki gözlem). |
| W1 | Windows Agent kurulum/enrollment + Windows Service | **PASS** | `NT SERVICE\BackupAgent`, `Auto`, `Running`; gerçek enrollment CLI + tek kullanımlık token. |
| W2 | DPAPI | **PASS** | `agent-identity.bin` DPAPI blob başlığı (`01000000d08c9ddf`), service restart sonrası aynı AgentId ile kimlik doğrulama (çözme kanıtı); önceki A0 probe'u plaintext yokluğu/corruption'ı ayrıca kapsıyordu. |
| W3 | Heartbeat + capability sync | **PASS** | `Online`, yeni heartbeat gözlendi; 14 capability (`mssql.backup.full`, `mssql.restore.*`, `backup.multi-destination`, `storage.download`, …), revizyon raporlandı. |
| W4 | MSSQL discovery | **PASS** | `restore-discovery` `Succeeded`, `canDiscover/canRestore=true`. |
| W5 | Gerçek Full Backup | **PASS** | İzole DB (508 KB) ve `master`; VM diskindeki dosyanın SHA-256'sı artifact checksum'ı ile eşit; staging'de artık dosya yok. |
| W6 | Restore (NewDatabase) | **PASS** | Precheck + restore `Succeeded`; kaynak ve restore `ONLINE`, `CHECKSUM_AGG` eşit. |
| W7 | İptal (çalışan ~648 MB backup) | **PASS** | Run `Cancelled`, artifact yok, staging/storage'a artık dosya kalmadı. |
| W8 | Service restart + recovery (çalışan backup sırasında) | **PASS** | Kesilen run 2 attempt ile `Succeeded`; aynı AgentId `online`, takip eden backup başarılı, staging temiz. |
| W9 | Ağ kesintisi (libvirt NIC `link down` ≥45 sn + `link up`) | **PASS** | Agent `offline` göründü, ağ dönünce `online`; kesilen run 2 attempt ile `Succeeded`; takip eden backup başarılı. Not: Windows Firewall outbound block kuralı kurulu bağlantıları kesmedi (agent online kaldı) — test tekniği, ürün hatası değil. |
| W10 | Temizlik | **PASS** | Service, `BackupAgentSmoke-*` dizini, güvenilen test sertifikası ve firewall kuralı VM'den kaldırıldı; disposable container/DB'ler silindi. |
| W11 | Windows Agent → Drive: Full Backup + ReadBack | **PASS** | Aynı dev tenant'a (`dev-windows-agent`, BackendRelayed, VM'deki SQL Server'dan) izole DB (508 KB): replica `Verified/ReadBack`, checksum eşit, staging temiz. |
| W12 | Windows Agent → Drive: backend download | **PASS** | SHA-256 ve uzunluk artifact ile eşit. |
| W13 | Drive replica'sından Windows Agent'ta restore (NewDatabase) | **PASS** | Precheck + restore `Succeeded`, `CHECKSUM_AGG` eşit. |
| W14 | ~154 MB Drive upload sırasında Windows service restart | **PASS** | Attempt 1 `AgentLost` (artifact `Deleted`), attempt 2 `Succeeded/Available`; indirme SHA-256 eşit; staging temiz. |
| W15 | Drive upload sırasında iptal | **PASS** | Run `Cancelled`, yarım artifact `Deleted`, staging temiz. |
| W16 | Windows Agent → Drive silme/cleanup | **PASS** | İki artifact `Deleted`. |

**Bulunan ve düzeltilen gerçek hatalar**

1. **Backend-relayed upload ~28,6 MB'ın üzerinde reddediliyordu.** `PUT /api/agent/tasks/{attemptId}/storage/content` Kestrel'in varsayılan request body limitine takılıyor; Agent'ta `StorageException: The storage relay is unavailable` → tüm attempt'ler `StorageUnavailable` ile 2 sn içinde başarısız. Google Drive/SFTP (BackendRelayed) için büyük her artifact etkileniyordu; önceki smoke'lar yalnız <1 MB artifact kullanmıştı. Düzeltme: endpoint'e `DisableRequestSizeLimitAttribute` (indirme endpoint'iyle aynı desen; sınır bildirilen uzunluk + attempt rezervasyonu + provider sınırıdır). Regresyon: `StorageRelayHardeningTests.RelayedUploadsAreNotCappedAtKestrelsDefaultRequestBodyLimit` (düzeltme öncesi kırmızı, sonrası yeşil); 122 MB gerçek Drive yüklemesiyle canlı doğrulandı.
2. **2026-10-13 akşamı yayımlanan NuGet advisory'leri (GHSA-334f-vvp4-7g6p, GHSA-846f-f764-76mx, GHSA-2fmv-rv2c-59x7) `-warnaserror` restore'u kırıyordu** (`Microsoft.AspNetCore.SignalR.Common` 10.0.12, `System.Security.Cryptography.Pkcs` 9.0.18; Agent publish/build NU1902/NU1903). Yeni bir Fast CI koşusu kırmızı olurdu. Düzeltme: `SignalR.Client` ve `Logging.Abstractions` 10.0.13, `Pkcs` için transitive pin 10.0.13 (`CentralPackageTransitivePinningEnabled`).

**Araç düzeltmeleri (ürün kodu değil):** `acceptance.py` taze job'ın boyut geçmişi olmadığı için `quota_size_unknown` ile reddediliyordu (sert quota tasarımı doğru); artık belgelenmiş explicit unlimited override ile önyükler ve plan limitini `finally` ile geri yükler. `restore` adımı kendi önceki `_restored` kopyasını düşürür. `ACCEPTANCE_CONNECTION_NAME` bağlantıyı adla seçer.

3. **Provider-bağımsız bağlantı testi mesajları SFTP'yi adlandırıyordu.** İptal edilmiş Drive token'ı için bağlantı testi `authentication_failed` döndürürken kullanıcıya "The SFTP credentials were rejected by the server." gösteriyordu (`Timeout`/`NetworkUnavailable` aynı sorunu taşıyordu). Mesajlar provider-nötr yapıldı; regresyon: `ConnectionTesterTests.SharedFailureMessagesDoNotNameOneProvider`.

**Google tarafı iptal gözlemi (D9'u tamamlar):** Bir bağlantıyı disconnect etmek (token revoke) Google'da aynı hesap + OAuth client için verilen **tüm** token'ları geçersiz kıldı; aynı hesaptaki diğer Drive bağlantısı da kendiliğinden `reauthorizationRequired=true` / `authentication_failed` oldu (ürün Google tarafı iptali doğru tespit etti). Operasyonel sonuç: aynı Google hesabına bağlı birden fazla tenant bağlantısı birbirinin disconnect'inden etkilenir; kullanıcı yeniden OAuth yaptı.

**Relay upload limit güvenlik incelemesi (PR öncesi):** Limit kalkınca üst sınırlar şunlardır: (1) `Content-Length` zorunlu ve pozitif; uzunluksuz (chunked) gövde provider'a ulaşmadan `400 invalid_request` (regresyon: `MalformedUploadsAreRejectedBeforeResolvingOrWritingStorage`); Kestrel gövdeyi bildirilen uzunlukla sınırlar. (2) Sonlu quota/rezervasyonda `claim.MaxArtifactBytes` aşan uzunluk gövde okunmadan `409 quota_exceeded` (`MultiDestinationRelayAuthorizationTests`). (3) Lease ve hedef kilit içinde yeniden doğrulanır; upload intent'i (orphan temizliği için) yazma öncesi kalıcılaşır ve kesilen attempt'in yarım artifact'ı canlı koşuda `Deleted` oldu. (4) Eşzamanlılık tenant başına 2, node başına en fazla 4 slot; Kestrel `MinRequestBodyDataRate` varsayılanı yerinde. (5) Drive: durable staging rezervasyonu + kapasite sınırı + `UploadTimeout` (1 sa); SFTP gövdeyi akıtır. **Kalan sınır:** `MaxArtifactBytes` yoksa (limitsiz plan/WarnOnly) üst sınır yalnız bildirilen uzunluk ve hedef kapasitesidir; kimliği doğrulanmış, lease sahibi agent bu durumda büyük gövde akıtabilir (Drive'da staging kapasitesi, SFTP'de uzak sunucu sınırlar). Agent credential'ı güvenilir ilke kabul edilir; ayrı bir mutlak üst sınır ürün kararıdır. **Drive disconnect UI uyarısı** mevcuttu ama paylaşılan Google grant'ını anmıyordu; onay diyaloğuna "aynı Google hesabını ve uygulamayı kullanan diğer bağlantıların da erişimi iptal olabilir" uyarısı eklendi (`google-drive.test.tsx`).

**NOT RUN / kalan riskler (A1 sınırı):** Windows Agent'tan **SFTP** (BackendRelayed) hedefi (Drive geçti, W11–W16); `multi-destination-acceptance.sh` (Drive + SFTP + local bir arada); backend restart'ının çalışan Windows run'ı üzerindeki etkisi; Windows'ta domain/özel service account ve overwrite-restore; Drive kapasite/kota aşımı; Drive'da gerçek ağ kesintisi (yalnız backend restart ve Windows service restart enjekte edildi); yük/throughput (en büyük artifact 154 MB Drive, ~648 MB Windows local); tam PostgreSQL suite'i (yukarıdaki NOT VERIFIED açığı sürüyor). Kullanıcının Drive'ında boş `Backup Platform/<tenant>` klasörleri kalır (yalnız uygulamanın oluşturduğu içerik silindi); dev stack'te `bp_google_drive_acceptance*` fixture veritabanları durur. Dev Windows Agent (`dev-windows-agent`, `scripts/windows/dev-windows-agent.sh uninstall` ile kaldırılır) VM'de kurulu bırakıldı; dev Windows Agent'ın servis kayıt defterinde (yalnız SYSTEM/Administrators erişimli) test SQL kimliği ve LocalMachine\Root'ta dev sertifikası bulunur.

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
| Google Drive | Completed (temel); canlı acceptance A1'de geçti | TenantManaged | OAuth ve resumable upload gerçek hesapla doğrulandı (Bölüm 1B). Provider kapasite izlemesi ayrı Faz 21. |
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
| Local / Central / SFTP / Google Drive storage | Completed (temel) | Google Drive canlı kabulü A1'de geçti (Linux ve Windows Agent; Bölüm 1B); Windows Agent'tan SFTP `NOT RUN`. |
| MSSQL Full Restore | Completed | API + Agent + UI; file restore ve chain/PITR ayrı faz. |
| Compression | Partial | MSSQL native compression var; File/Folder artifact compression bekliyor. |
| Multi-destination ve replica repair | Completed (temel) | Quarantine, atomic admission, doğrulanmış kopya sayısı; tam 3-2-1 policy ayrı faz. |
| Quota, Plans, Usage ve Support Access | Completed (temel) | Satış yüz yüze; online billing yapılmayacak. İzole support restore ortamı ayrı Future. |
| File/Folder backup ve include/exclude | Planned | Faz 3, yalnız proje iskeleti. |
| Bandwidth limiting / transfer policies | **Completed (2A, çalışma dalı) / Planned (2B/2C)** | Faz 2A: agent/tenant/job byte-temelli upload limiti (Bölüm 4); 2B/2C ihtiyaca göre daha sonra. |
| MSSQL Differential/Transaction Log/PITR | Planned | Faz 10–12. |
| PostgreSQL backup | Planned | Faz 13. |
| S3 / SMB/NAS / FTPS | Planned | Faz 16/18 ve provider genişlemesi. |
| OneDrive / Dropbox | Future | 2A.12. |
| Email/webhook/SMS notification (N1) | Completed — Email/Webhook; SMS `IMPLEMENTED — NOT VERIFIED` | Faz 24 içindeki N1 dilimi (Bölüm 26); Teams/Slack/Telegram/push sonraya. |
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

**Durum: Faz 2A tamamlandı (çalışma dalı); 2B/2C Planned.** API request rate limitleri ve transfer eşzamanlılık bütçesi mevcut olsa da bunlar **byte/saniye trafik sınırlaması değildir**. Mevcut `IStorageProvider`/AgentDirect/BackendRelayed yolları incelenerek, MSSQL backup, replica repair, download/restore ve retention güvenliği bozulmadan bu faz parçalara ayrılır.

## Faz 2A — Byte-temelli upload bandwidth limiting (**Completed — 2026-10-14, çalışma dalı `feature/faz-2a-upload-bandwidth-limiting`; uygulama kaydı aşağıda**)

- Agent, tenant ve job bazlı hız sınırları için açık policy hiyerarşisi ve override kuralları (birim **bit/s veya byte/s net tanımlı**, `Unlimited` destekli).
- Aynı tenant'ın paralel upload'larının toplam sınırı aşmaması; yalnız request başına throttle uygulamak yeterli değildir.
- AgentDirect ve BackendRelayed topolojilerinde sınırın hangi noktada uygulanacağı açıkça belirlenir; relay ile çift throttle veya kota/limit atlatma olmamalıdır.
- Tekrar kullanılabilir ve düşük ek bellek kullanan, **streaming** throttle tasarımı: içerik RAM'e alınmamalı, büyük .bak dosyası diskten tekrar kopyalanmamalı.
- Limitin yalnız **upload transfer byte'ları** için uygulanması; DB backup, checksum, heartbeat, lease yenileme, admin API ve kontrol mesajları etkilenmemeli.
- Cancellation ve agent/backend restart durumlarında permit/stream/lease kaynakları kapanmalı; transferin doğruluğu ve durably tracked partial nesne garantileri korunmalı.
- İleride başka backup engine ve storage provider ile yeniden kullanılabilir olmalı; yeni provider zorunlu interface metotları olmadan desteklenmeli.
- Default davranış mevcut sınırsız aktarımın geriye dönük karşılığı olmalı; tenant izolasyonu ve admin yetkileri korunmalı.

**Faz 2A kabul senaryoları:** Kontrollü saat/byte ölçümü ile düşük/orta/büyük stream'de üst hız sınırı; iki paralel upload'ın aynı tenant toplam sınırını aşmaması; farklı tenant'ların birbirinden bağımsız davranması; job override precedence; sınırsız mod regresyonu; cancel/retry/restart/lease kaybı; SFTP + backend relay yolları; aynı içerik için checksum ve boyut doğrulamasının değişmemesi. Başlangıçta deterministik test, ardından izole ağ smoke kullanılır.

### Faz 2A — Uygulama kaydı, kararlar ve kanıt

**Politika modeli.** Üç bağımsız **tavan** vardır: *tenant* (tenant'ın tüm eş zamanlı upload'larının toplamı, tüm Agent'lar dahil), *agent* (o Agent'ın upload'ı) ve *job* (o job'ın bir çalıştırması). Hepsi aynı anda geçerlidir; **en kısıtlayıcı olan kazanır**. Job limiti yalnız sıkılaştırır, agent/tenant tavanını aşamaz ("override" gevşetme değil sıkılaştırma anlamındadır; limit atlatma yolu yoktur). `Unlimited` = o kapsamda satır yok (`null`); sihirli sayı yoktur. Birim **bayt/saniye, ondalık**: 1 Mbit/s = 125.000 B/s, 1 MB/s = 1.000.000 B/s; izin verilen aralık **16.000 B/s – 100.000.000.000 B/s** (alt sınır, proxy/idle timeout'larını ve Kestrel'in 240 B/s minimum veri hızını rahat aşar). Limitler `upload_bandwidth_limits` tablosunda `(TenantId, Scope, ScopeId)` anahtarıyla tutulur; değişiklik tenant Owner/Admin'e aittir, aynı transaction'da audit olayı (`upload_bandwidth.limit_changed`, önceki/yeni değer) yazılır, başka tenant'ın agent/job'ı `404` döner. Limit değişikliği çalışan upload'a en geç yenileme aralığı (tenant limiti varken 3 sn, yoksa 15 sn) içinde ulaşır; run snapshot'ına kopyalanmaz.

**Uygulama noktası (çift throttle yok; 2A.1 ile güncellendi).** Sınırı **baytları okuyan taraf** uygular, böylece her bayt tam bir kez sınırlanır: **BackendRelayed** (SFTP, Google Drive) hedeflerde backend relay `PUT …/storage/content` gövdesini sarar (agent'a bağlı değildir; eski veya değiştirilmiş bir agent atlayamaz), **AgentDirect** (Local/Central) hedeflerde backend baytları görmediğinden Agent artifact akışını `IStorageProvider.PutAsync`'e vermeden sarar. Aynı `UploadPacer` iki tarafta kullanılır. Agent, relayed bir hedef için yalnız politikayı bir kez okur (`Observe`, slice almaz) ve deadline'ını genişletir. Artifact diskten tekrar kopyalanmaz, RAM'e alınmaz; yalnız upload bayt'ları sınırlanır (DB backup, checksum, heartbeat, lease yenileme, admin API, read-back doğrulama ve restore/download etkilenmez). Sınırlayıcı (`TransferRateLimiter`) sürekli dolan token bucket'tır: küçük ve düzenli dilimler (≤ 64 KiB, hızın onda biri), alt sınır `rate × T + kova` (kova ≈ bir dilim + 50 ms), çalışırken hız değiştirilebilir, `null` ise bare-stream yolu. Agent bir backup'ı seri yürüttüğü için agent tavanı o upload'a bütün olarak uygulanır.

**Tenant toplamı ve çok düğümlü koordinasyon.** Tenant tavanı, tenant'ın canlı upload'ları arasında **veritabanında** bölünür: AgentDirect Agent'ı `POST /api/agent/tasks/{attemptId}/upload-policy` ile (lease-fenced), relay ise doğrudan veritabanından **upload başına** dilimini alır ve yeniler (`upload_bandwidth_grants`, TTL 10 sn = üç kaçan yenileme). Karar tenant başına `pg_advisory_xact_lock` altında verilir; hiçbir düğüm bellekte durum tutmaz. Dilim = eşit pay, fakat **diğerlerinin verilmiş dilimlerinden kalan** miktarı aşamaz: yeni gelen upload, mevcut upload küçültülmüş dilimini alana kadar `0` alır ve **beklenir** (böylece verilen dilimlerin toplamı hiçbir adımda tenant limitini aşmaz; rastgele geliş/gidiş dizileriyle testli). Bitmiş/lease'i düşmüş attempt'in dilimi anında serbest kalır; Agent normal bitişte dilimi geri verir; çöken Agent'ın dilimi TTL ile kendiliğinden düşer. Dilim yenilenemezse (backend ulaşılamaz, TTL doldu) Agent aktarımı **durdurur** (backend dilimi başkasına vermiş olabilir); lease reddedilirse aktarım bir sonraki okumada hata verir. Slice yokken (0) upload **başlamaz**: relay pacer'ı admission ve depolama kilidinden ÖNCE alır, slice bekleme hiçbirini tutmaz (açık ama bayt akmayan istek Kestrel/proxy minimum-veri-hızı kontrolüne takılırdı). Tenant limiti olmayan upload'lar tenant kilidini hiç almaz. Job'un hız sınırı yüzünden kullanamadığı pay yeniden dağıtılmaz (eşit bölüşüm).

**Başlangıç güvenliği ve uyumluluk.** Politika alınamazsa upload **başlamaz** (fail-closed; `upload-policy-unavailable`, `StorageUnavailable`). Limiti uygulayamayan Agent'a, Agent'ın kendisinin sınırlaması gereken bir run verilmez: capability `backup.upload-bandwidth-limit` claim sırasında yalnız **Agent-yazan (AgentDirect) hedefi olan** ve limit geçerli run'larda aranır; backend'in relay ettiği hedefler her Agent'a verilebilir. Limit olmayan run'lar etkilenmez. Limitsiz kullanımda akış pass-through'dur.

**Timeout etkileşimleri.** Agent hedef timeout'u (varsayılan 6 sa) ve backend provider'ların toplam-aktarım sınırları (Google Drive `UploadTimeout` 1 sa, SFTP `TransferTimeout` 1 sa) kasıtlı yavaşlatılmış upload'u "takılmış" saymasın diye `size / en-düşük-hız` kadar (en çok 24 sa) genişletilir (`UploadPolicyResponse.DeadlineAllowance`; relay bunu kendi pacer'ının en-düşük hızından hesaplar, Agent'ın beyanına güvenmez; `StoragePutOptions.TransferAllowance` — yeni provider zorunlu metot eklenmeden). En düşük hız: job/agent limiti olduğu gibi, tenant limiti `limit / tenant eşzamanlı backup üst sınırı`. İdle, istek-başı ve bağlantı timeout'ları genişletilmez. Kestrel `MinRequestBodyDataRate` (240 B/s, 5 sn tolerans) 16 KB/s alt sınırdan çok düşüktür; gerçek Kestrel üzerinde 5 sn'yi aşan en düşük limitli upload testle doğrulandı.

**Kapsam dışı (bilinçli).** Faz 2B/2C; restore/download; backend kaynaklı replica repair upload'ı (veri merkezi trafiği, müşteri uplink'i değil); provider API rate limitleri. Politika backup engine'inden ve provider'dan ayrıdır: yeni engine/provider `IUploadBandwidth` oturumunu pipeline'dan alır, interface'e metot eklenmez.

**Arayüz.** `GET /api/tenants/{id}/upload-bandwidth` (Viewer+), `PUT …/upload-bandwidth/tenant`, `…/agents/{agentId}`, `…/jobs/{jobId}` (Admin+; gövde `{ "bytesPerSecond": n | null }`; aralık dışı `400 out_of_range`). Frontend: yan menüde **Upload Bandwidth** (tenant kartı, agent ve job tabloları; Mbit/s, MB/s, KB/s, B/s birimleri; bit/bayt karışıklığına karşı her zaman B/s karşılığı gösterilir).

**Doğrulama (2026-10-14, 2A ilk teslim; 2A.1 sonuçları aşağıda).** Release build (`-warnaserror`) temiz; unit **3769**, architecture **123**, Fast Agent **373** PASS; frontend **58 dosya / 564 test**, typecheck/lint (yalnız önceden var olan 2 uyarı)/test PASS. Odaklı PostgreSQL seçimi (Audit, Migration, BackupConcurrency, StorageRelay, StorageTransferAdmission, AgentCapability, BackupOrchestration, BackupRetry, MultiDestination, BackupOverlap, HardQuota ve yeni UploadBandwidth) **418/418 PASS** (UploadBandwidth: 11 store + 14 API/relay/Kestrel testi bu seçimin içindedir); son kod durumunda UploadBandwidth/StorageRelay/AgentCapability/BackupConcurrency/MultiDestination/Audit seçimi ayrıca yeniden çalıştırıldı (**221/221 PASS**). Migration `UploadBandwidthLimits` yalnız ekleyicidir (iki yeni tablo, veri değişmez): `has-pending-model-changes` temiz, `Up → Down → Up` ve idempotent script boş veritabanında PASS. Yeni testler: limiter (alt/orta/üst hız, paylaşım, hız değişimi, held, Fail, iptal; FakeTimeProvider), stream (bayt eşitliği, refund, passthrough), politika matematiği (rastgele dizilerde Σslice ≤ limit), service, governor (fail-closed, yenileme, TTL aşımı, lease kaybı, release, slice bekleme), pipeline (aynı baytlar, fail-closed, deadline genişletme), store (tenant izolasyonu, audit, lease/expiry/terminal, çok-düğüm eşzamanlı yenileme, claim kapısı), API (roller, aralık, 404, policy çağrısı, relay allowance) ve gerçek-Kestrel smoke.

**İzole transfer smoke'ları.** (1) Gerçek Linux Agent + gerçek MSSQL + gerçek HTTPS API, Local depolama: 1.748.992 B artifact, job limiti 250.000 B/s (2 Mbit/s) → upload **6,99 sn** (tavanın izin verdiği en hızlı 6,85 sn; limitsiz 0,11 sn), SHA-256 kaynak/artifact/replica eşit. (2) Gerçek Linux Agent → BackendRelayed → gerçek SFTP (`BP_SMOKE_UPLOAD_LIMIT=64000`, `run-sftp-smoke.sh`): 532.480 B, **11,0 sn** (en hızlı izinli 8,22 sn), remote dosya/indirme/restore/retention akışı **SFTP_SMOKE_OK**. (3) Gerçek Kestrel + relay endpoint: 96.000 B, 16.000 B/s, >5 sn, bayt-bayt aynı. (4) Arayüz gerçek API'ye karşı tarayıcıda: tenant 10 Mbit/s → `1.250.000`, job 4 Mbit/s → `500.000`, aralık dışı değer istemcide engellendi.

**Performans (yerel, sayfa önbelleğinde 1 GiB dosya, 64 KiB okuma).** Ham `FileStream` ≈ 5,3–6,2 GB/s; limitsiz `ThrottledStream` ≈ 5,4–6,1 GB/s (ölçüm gürültüsü içinde: −4,7 / +12,2 / +0,9 %); limit 100 GB/s (sınırlı yol) ≈ 4,6–5,6 GB/s. Hız doğruluğu: 10 Mbit/s → %101,0, 100 Mbit/s → %100,5, 1 Gbit/s → %100,6 (başlangıç kovası kaynaklı +%0,5–1); tek başına limiter 12,5–500 MB/s arasında %101,1–101,2. 1 GiB'lik limitsiz geçişte ek bellek yok (toplam ayrılan ≈ 16 MB, gen0 GC 0–1). İki ölçümde bulunan hata düzeltildi: `TimeSpan` kesmesi yüksek hızda ~%12 hız kaybı yapıyordu; sub-ms bekleme meşgul-döngüye dönüyordu (min. 2 ms bekleme).

**Kalan riskler / doğrulanmayanlar.**
- **AgentDirect hedefler işbirlikçidir** (2A.1 sonrası yalnız onlar): Local/Central baytlarını backend görmediğinden, değiştirilmiş bir Agent politikayı yok sayabilir; capability kapısı yalnız dürüst-eski Agent'ları ve claim anındaki limiti kapsar. Relayed hedeflerde sınır Agent'a bağlı değildir (canlı kabulde eski Windows/Linux Agent'larla doğrulandı).
- **Toplam sınırı, Agent'lar dilimi uyguladığı ölçüde kesindir:** kayıtlı dilimlerin toplamı limiti aşmaz, ancak yanıtın kaybolması/uygulama gecikmesi (ms–sn) anlık sapma yaratabilir; kullanılmayan pay yeniden dağıtılmaz.
- **Uzun aktarımlar kaynak tutar:** yavaşlatılmış relay upload'ı süresince relay admission yuvası (tenant 2, düğüm 4) tutulur (2A.1: PostgreSQL bağlantısı/advisory lock artık tutulmaz); 24 sa allowance üst sınırını aşacak boyut/limit kombinasyonları provider timeout'una takılır. Aktarım ortasında backend 10 sn'den uzun ulaşılamazsa gövde akmaz ve relay/proxy isteği düşürebilir (hedef başarısız, retry politikası devralır; güvenli yön).
- **Canlı Drive ve Windows limitle çalıştırılmadı (NOT RUN):** Drive chunk okumasının düşük limitte (16 KB/s'de 1 MiB ≈ 64 sn) davranışı yalnız kod incelemesiyle değerlendirildi `[INFERENCE]`; SFTP gerçek, Drive yalnız relay/deadline testleriyle kapsandı. A1'in diğer NOT RUN kalemleri (Windows→SFTP, multi-destination acceptance, backend restart ile Windows run, Drive kapasite/ağ kesintisi, 154 MB üzeri yük) **aynen geçerlidir**.
- **Tam PostgreSQL suite'i yeniden çalıştırılmadı** (A0'daki 30 dakikalık zaman aşımı açığı sürüyor); doğrulama odaklı seçimlerle yapıldı. Advisory-lock'un gereği mutasyon testiyle ayrıca ispatlanmadı.
- Yetenek satırı henüz yokken run başlatmanın `409 agent_multi_destination_unsupported` vermesi 2A'dan öncedir ve 2A.1'de araştırıldı (aşağıda).

### Faz 2A.1 — Sertleştirme ve canlı kabul (**Completed — 2026-10-14, aynı çalışma dalı**)

**1. Uzun upload ve PostgreSQL bağlantısı/advisory lock (bulgu ve düzeltme).** Önceki relay `PUT` her depolama işlemini (upload/verify/delete, restore verify) `pg_advisory_lock` ile **havuzdaki bir bağlantıya bağlı session kilidi** içinde yürütüyordu; yavaşlatılmış upload saatlerce bir bağlantıyı ve kilidi tutabilirdi (AGENTS.md: yavaş harici I/O boyunca kilit tutma). Etki üst sınırlıydı (admission: düğüm başına ≤ 4, tenant başına 2 yuva; havuzdan başlık bırakır) ama her yavaş upload bir bağlantıyı tutuyor, çöken sürecin kilidi yalnız bağlantı kopunca düşüyordu. **Düzeltme:** kilit, anahtar başına **kısa ömürlü veritabanı lease'i** (`backup_transfer_leases`, `LockId` = önceki hash) oldu: edinme tek `INSERT … ON CONFLICT … WHERE ExpiresAt <= clock_timestamp()` (yalnız **DB saati**), tutan taraf 10 sn'de bir yeniler (TTL 30 sn), yenileyemezse kendi işini lease dolmadan 10 sn önce iptal eder (`Lost` → `409 lease_conflict`), çöken tutanın lease'i süresiyle düşer, normal bitişte satır silinir. Retention claim'i satırı yalnız kendi transaction'ı için (süresi 0, commit'e kadar kilitli) alır; canlı lease varsa o replica'yı atlar (`pg_try_advisory_xact_lock` ile aynı anlam). Fencing değişmedi: her işlem lease'i aldıktan sonra attempt lease'ini yeniden doğrular; tenant izolasyonu aynı (bağlantı/anahtar kimliğiyle). Eski pinned-session testleri (unlock failure, pool-uncertain) kaldırıldı; yerlerine lease testleri geldi. Admission yuvaları DB bağlantısını artık tutmaz ama upload süresince yuvayı tutmaya devam eder.

**2. Cooperative throttling sınırı.** Yukarıda ("Uygulama noktası"): BackendRelayed için sınır artık **backend'de** zorlanır (agent atlayamaz, çift throttle yok: agent relayed hedefi sarmaz, relay tek sarar; pacer admission/kilitten önce alındığı için bekleme deadlock/yuva tutmaz). AgentDirect hedefler (backend baytları görmez) kalıcı olarak işbirlikçi kalır; bu mimari sınırdır, riskler bölümünde.

**3. `agent_multi_destination_unsupported` ve capability sync.** Admission, Agent'ın capability snapshot'ında `backup.multi-destination` yoksa fail-closed reddeder; gerçek host ilk heartbeat'ten sonra capability'lerini senkronlar. Gözlem (`MsSqlCapabilitySyncSmokeTests`, gerçek host + API): host başlamadan run başlatmak `409 agent_multi_destination_unsupported`; **host başladıktan 0,6 sn sonra** aynı istek `200`. Yani gecikme küçüktür ve tasarım gereğidir; asıl sorun mesajın "Agent'ı güncelleyin" demesiydi (mesaj artık "eski olabilir veya bağlantısını henüz tamamlamamış olabilir; kısa süre sonra yeniden deneyin" der, API ve UI). **Regresyon değerlendirmesi:** 2A öncesi commit (`d869d523`) ayrı worktree'de derlenip çalıştırıldığında mevcut `MsSql*AgentSmokeTests` **aynı `409` ile zaten başarısızdı** (testler run'ı Agent senkronundan önce başlatıyordu) → 2A'dan kaynaklanan regresyon **yok**. Testler fixture ile capability beyan eder; gerçek senkron ayrı testle doğrulanır. Tüm `AgentSmokeTests` + sync testi **17/17 PASS** (gerçek Linux Agent + MSSQL).

**4. Canlı kabul: Windows VM Agent → gerçek Google Drive (+ Linux dev Agent).** `scripts/windows/run-bandwidth-acceptance-drive.sh` (yeni). Dev API 2A.1 build'ine yeniden dağıtıldı (iki ek migration dev DB'ye uygulandı); **Windows ve Linux Agent'lar bilerek eski build'de bırakıldı** (mevcut enrollment ve Drive bağlantısı korundu): bu, relay enforcement'ın Agent'tan bağımsız olduğunun canlı kanıtıdır.

| # | Senaryo | Sonuç | Ölçüm |
|---|---|---|---|
| B0 | Preflight (API, Drive bağlantısı, VM, SQL, limits API) | **PASS** | Agent'lar online; yeni capability'yi bildirmiyor (beklenen) |
| B1 | Baseline, limitsiz Drive upload (12,4 MB) | **PASS** | Relay upload **36,63 sn ≈ 338 KB/s** (Windows VM → backend → Drive yolunun kapasitesi); read-back + diğer **3,08 sn** (destination penceresi 39,71 sn); `Verified/ReadBack` |
| B2 | Düşük limit 100.000 B/s (4,47 MB) | **PASS** | Relay upload **59,37 sn ≈ 75,3 KB/s** (alt sınır 33,2 sn, üst 142,2); read-back + diğer 1,92 sn |
| B3 | Orta limit 200.000 B/s (12,4 MB) | **PASS** | Relay upload **80,95 sn ≈ 153 KB/s** (alt sınır 55,5 sn); read-back + diğer 3,01 sn. (İlk deneme 1.500.000 B/s ile **FAIL** göründü: limit yol kapasitesinin üstündeydi, parametre geçersizdi; kapasite altında tekrarlandı) |
| B4 | Paralel: tenant limiti 400.000 B/s, Windows + Linux Agent aynı anda (2 × 8,45 MB) | **PASS** | Relay upload 66,2 / 63,7 sn (≈ 128 + 133 KB/s), çakışma 65,8 sn; 122 örnekte **slice toplamı en çok 400.000 = limit**; staging temiz |
| B5 | Paced upload sırasında iptal (64.000 B/s) | **PASS** | Run `Cancelled`, yarım artifact `Deleted`, staging temiz, grant satırı 0; **upload sürerken advisory lock 0, canlı lease 1; sonra 0/0** |
| B6 | Paced upload sırasında Windows service restart + retry | **PASS** | Attempt 1 `AgentLost` (artifact `Deleted`), attempt 2 `Succeeded/Available`; relay upload **80,33 sn ≈ 55,7 KB/s** @ 64.000 B/s; indirme SHA-256 eşit |
| B7 | Cleanup | **PASS** | Limitler kaldırıldı, artifact'lar silindi, izole DB'ler düşürüldü; **API job silmediği için `BW accept *` job'ları devre dışı kaldı** |
| B8 | Gerçek Linux Agent → relay → gerçek SFTP, 64.000 B/s | **PASS** | 532.480 B, 12,01 sn (en hızlı izinli 8,22 sn); download/restore/retention `SFTP_SMOKE_OK` |
| B9 | Gerçek Agent + MSSQL, Local (AgentDirect), 250.000 B/s | **PASS** | 1.748.992 B, 7,00 sn (izinli en hızlı 6,85; limitsiz 0,12 sn), SHA-256 eşit |
| B10 | 16.000 B/s (en düşük) canlı Drive | **NOT RUN** | En düşük canlı Drive hızı 64.000 B/s'tir (yalnız gerçek Kestrel ve SFTP'de 16.000 denendi) |
| B11 | Windows Agent → Agent-yazan (AgentDirect) hedef + limit | **NOT RUN** | Windows Agent eski build'de bırakıldı; Agent-tarafı pacing yalnız Linux'ta kanıtlı (B9) |
| B12 | Backend restart'ı çalışan Windows run'ı üzerinde; slot (429) doygunluğu canlı | **NOT RUN** | A1 NOT RUN kalemleri aynen geçerli |

**Doğrulama (2A.1).** Release build `-warnaserror` temiz; unit **3769**, architecture **123**, Fast Agent **375**, frontend **564** PASS. Odaklı PostgreSQL: lease/relay/retention/UploadBandwidth/MultiDestination seçimi **224/224**, geniş regresyon seçimi (Audit, Migration, BackupConcurrency, AgentCapability, BackupOrchestration, BackupRetry, MultiDestination, BackupOverlap, HardQuota, FixtureIsolation, Google, Download, Restore) **654/655 → bulunan tek hata (retention niyet testinin advisory-lock'a bağlı olması) lease'e uyarlanıp 20/20 PASS**; yeni `TransferLeases` migration'ı için `has-pending-model-changes` temiz, `Up → Down → Up` PASS. Yeni testler: ortak `UploadPacer`/governor (relayed hedefi sarmaz, Observe), relay'in gövdeyi kendisinin sınırlaması (istemci sınırsız), gerçek Kestrel'de relay-okumalı en düşük limit, slice bekleme, lease kaybında hiçbir bayt okunmaması, lease servisi (dışlama, yenileme, çökmüş tutan, takeover, DB kopması self-fence, süpürme), admission (bağlantı/kilit tutulmaz, lease alınırsa 409), claim kapısı yalnız AgentDirect, gerçek capability sync.

**Kalan riskler (2A.1 sonrası).**
- **Lease dışlaması süreyle sınırlıdır:** session kilidi katı karşılıklı dışlama veriyordu; lease'te tutan yenileyemezse 10 sn içinde kendini iptal eder, fakat sağlayıcı çağrısı iptali bu sürede dinlemezse, lease dolduktan sonra ikinci bir yazıcı başlayabilir. Azaltıcılar: deterministik temp + atomik publish, boyut/checksum doğrulaması, her işlemde attempt-lease fencing, retention'ın lease'e saygısı. Mutasyon/kaos testiyle (donmuş provider) ispatlanmadı.
- **AgentDirect hedeflerde sınır Agent'a güvenir** (backend baytları görmez).
- **Yuva doygunluğu:** yavaşlatılmış relay upload'ı admission yuvasını (tenant 2, düğüm 4) tutar; bir tenant'ın iki yavaş upload'ı kendi verify/delete işlemlerini `429` ile geciktirebilir. DB bağlantısı tutulmadığı için yuva sayıları ileride güvenle artırılabilir; bu işte değiştirilmedi ve canlı doygunluk ölçülmedi (B12).
- Canlı ölçümlerin Windows→Drive yolu ≈ 324 KB/s'de doygun; yüksek hız (MB/s) limitlerinin doğruluğu canlı Drive'da gösterilemedi (yerel ölçüm: %100,5–101).
- Dev tenant'ında çok sayıda `BW accept *` job'ı devre dışı olarak kaldı (job silme API'si yok); silinmesi PlatformAdmin DB işlemi gerektirir.
- Tam PostgreSQL suite'i yeniden çalıştırılmadı (A0 zaman aşımı açığı sürüyor).

**Faz 2A kararı: READY (kabul koşullarıyla).** Faz 2A kabul senaryolarının tümü (üst hız sınırı, paralel tenant toplamı, tenant bağımsızlığı, job precedence, limitsiz regresyon, cancel/retry/restart/lease kaybı, SFTP + relay, checksum/boyut değişmezliği) deterministik testle ve gerçek akışlarla kanıtlandı; NOT RUN kalemleri (B10–B12) kabul kriteri değil, risk olarak kayıtlıdır.

### Faz 2A — Nihai inceleme (PR öncesi; **Completed — 2026-10-14**)

**Gerçek hata bulgusu ve düzeltmeler** (her biri için regresyon testi eklendi):
1. **Slice attempt başına tutuluyordu (tenant toplamı atlatılabilirdi).** Bir attempt'in birden çok hedefe aynı anda upload etmesi (değiştirilmiş Agent veya çok-hedefli attempt) her upload'a tenant limitinin tamamını verirdi (hedef sayısı kadar kat). Slice artık **upload başına** (`UploadId`, relay'de her istek ayrı); yeni migration `UploadGrantPerUpload` (canlı grant'lar 10 sn'lik lease olduğundan silinip yeniden istenir). Test: aynı attempt'in iki eşzamanlı upload'ı limiti paylaşır, toplam ≤ limit.
2. **Transfer lease fence'i gevşeyebiliyordu (P1).** Self-fence kontrolü yenileme döngüsü içindeydi; takılan bir yenileme `Ttl − margin + RenewEvery` ≈ `Ttl` kadar sürebilir, tutan lease dolduktan sonra da yazabilirdi. Fence artık **bağımsız bir zamanlayıcıdır** (`CancelAfter`, yalnız başarılı yenilemede yeniden kurulur; süre isteğin yapıldığı andan sayılır) ve normal bitişte release her zaman sahip-kontrollüdür. Test: yenilemesi askıda kalan holder `Ttl − margin` içinde `Lost` alır.
3. **Pacer'da takılan yenileme slice süresini yok sayıyordu (P2).** `FetchAsync` zaman sınırsızdı; slice sahibi tarafından başkasına verildikten sonra da transfer sürebilirdi. Yenileme artık `validUntil` ile sınırlıdır (`WaitAsync`), süre dolunca transfer tutulur; slice süresi cevabın gelişinden değil sorunun sorulduğu andan sayılır. Test: hiç cevaplanmayan yenileme transferi tutar.
4. **Retention'ın lease süpürmesi kilitlenebilirdi (P3).** Her take'in `DELETE`'i artık `FOR UPDATE SKIP LOCKED` altında (retention transaction'ı satırları commit'e kadar kilitli tutar; yazıcıların süpürmesi onları atlar).
5. **Grant süreleri düğüm saatine bağlıydı (P4).** Çok düğümlü koordinasyonda ileri giden bir saat başka düğümün canlı slice'ını süresi dolmuş sayardı. Grant budama/süre/`ExpiresAt` artık **veritabanı saatiyle** (`clock_timestamp()`); attempt lease'i diğer yaşam döngüsü çağrıları gibi çağıranın saatiyle. Test: bir dakika ileri saatli düğüm canlı slice'ı süresi dolmuş saymaz.

**İnceleme ile doğrulananlar (hata değil).** `INSERT … ON CONFLICT DO UPDATE … WHERE` READ COMMITTED'da retention'ın satır kilidini bekler ve commit sonrası süresi dolmuş satırı alır; rollback'te düz insert başarılır (kayıp güncelleme yok). Yazıcı lease aldıktan sonra attempt/restore lease fencing'ini yeniden yapar (retention `DeletePending` yazdıysa reddeder). Retention canlı lease'i atlar.

**BackendRelayed enforcement ve AgentDirect sınırı.** Relay `PUT`'u gövdeyi kendisi sınırlar; testle: istemci hiç pace etmeden (eski/değiştirilmiş Agent gibi) gerçek Kestrel üzerinden limitin altına inemez; canlıda eski Windows/Linux Agent'larla PASS. Bypass vektörleri tarandı: (a) pace etmemek → relay sınırlar; (b) aynı attempt'te paralel hedef upload'ları → upload başına slice (düzeltme 1); (c) lease'siz/yanlış lease ile upload → pacer lease'i doğrular, bayt okunmaz. **Kalan sınır:** *job* ve *agent* limitleri **upload başınadır**: dürüst Agent seri çalıştığı için tam; değiştirilmiş bir Agent aynı attempt'in hedeflerini paralel yüklerse job/agent tavanı hedef sayısıyla (admission yuvasıyla sınırlı, tenant başına 2) çarpılabilir, tenant toplamı yine tam kalır. **AgentDirect** (Local/Central) hedeflerde bayt backend'e uğramaz: sınır tamamen Agent işbirliğine dayanır; capability kapısı yalnız dürüst-eski Agent'ı, fail-closed başlangıç yalnız politikasız başlamayı önler; değiştirilmiş bir Agent'ı backend doğrulayamaz (mimari sınır, kabul edildi).

**Hız ölçümü: gerçek upload süresi ile read-back ayrıldı.** Relay artık `Relayed upload finished: attempt … bytes … seconds …` satırını yazar (yalnız relay gövde → provider süresi; slice beklemesi ve ayrı `VerifyAsync` read-back'i hariç). Kabul betiği limitleri bu süre üzerinden değerlendirir (alt sınır `(boyut − kova)/limit`). Sonuç: **read-back + diğer yalnız 1,9–3,1 sn**; önceki raporlardaki "limitin %75'i" read-back'ten değildi. Gerçek upload hızı limitin **%75 (100 KB/s), %77 (200 KB/s), %87 (64 KB/s)**'ında kaldı, yani limit **hiç aşılmadı** (alt sınır testi geçti) ama dolmadı. `[INFERENCE]` neden: Google Drive provider'ı 1 MiB'lik chunk'ı önce (pace edilmiş) gövdeden okuyup sonra Drive'a gönderiyor; okuma ve gönderme art arda olduğundan etkin hız `1/(1/limit + 1/R_drive)`'a düşer (R_drive ≈ 300–650 KB/s ile üç ölçüm de uyumlu). Bu, Agent → backend hattının (müşterinin WAN'ı) tam limitte aktığını değil, **bir üst sınır** uygulandığını gösterir; SFTP gibi akış yapan provider'larda %100'e yakın beklenir (yerel ölçüm: %100,5–101; SFTP canlı: 12,0 sn ≥ alt sınır 8,2 sn). Chunk/akış çakıştırma bu işte yapılmadı.

**Doğrulama (son kod).** Release build `-warnaserror` temiz; unit **3770**, architecture **123**, Fast Agent **375**, frontend **564** (typecheck, lint [yalnız önceden var olan 2 uyarı], build PASS). Odaklı PostgreSQL (UploadBandwidth, BackupTransferLease, StorageTransferAdmission, StorageRelay, Retention, MultiDestination, Audit, Migration, BackupConcurrency, AgentCapability, BackupOrchestration, BackupRetry) **496/496**; MSSQL Agent smoke'ları (`AgentSmokeTests` + gerçek capability sync) **17/17**; `UploadGrantPerUpload` için `has-pending-model-changes` temiz, Up/Down/Up ve idempotent script PASS. Canlı Windows/Linux → Drive kabulü yeni ölçümle yeniden çalıştırıldı: baseline, düşük, orta, paralel tenant, iptal, retry **8/8 PASS** (yukarıdaki B tablosu). **NOT RUN (korunur):** B10 (16.000 B/s canlı Drive), B11 (Windows'ta AgentDirect + limit), B12 (backend restart / canlı 429 doygunluğu), tam PostgreSQL suite, donmuş-provider chaos testi.

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

**Durum: Completed (Google Drive temel provider); canlı acceptance A1'de geçti (2026-10-14).** Tenant-owned OAuth bağlantısı, güvenli token yönetimi, staging ve resumable upload gerçek hesapla, hem Linux hem Windows Agent'tan doğrulandı (Bölüm 1B; 122 MB upload bug'ı düzeltildi; Google tarafı token iptali tespit edildi). Drive kapasite/kota aşımı ve Drive'da gerçek ağ kesintisi `NOT RUN`; provider-reported kapasite izleme Faz 21'e kalır.

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

**Durum: N1 Completed (Email + Webhook doğrulandı), SMS `IMPLEMENTED — NOT VERIFIED` — 2026-10-14, çalışma dalı `feature/n1-notifications`.** Teams/Slack/Telegram/push bu fazın sonraki adımlarıdır (**Planned**, N1 kapsamı dışı). Uygulama kaydı, kararlar ve kanıt aşağıdadır.

**N1 (teslim):** başarısız veya uzun süredir başarılı olamayan backup, offline agent, kritik kota ve replica yetersizliği/degraded olaylarını **email, generic HTTPS webhook ve SMS** üzerinden tenant politikasına göre ulaştırma; durable event/outbox, delivery history, retry/backoff, dedup/suppression, burst koruması ve kanal devre dışı bırakma/silme. Güvenlik: SSRF/DNS-rebinding/private-IP/redirect önlemleri, secret redaction, tenant izolasyonu. **N1 kabulü:** başarısız gönderim, aynı olayın tekrar gelmesi, tenant/policy değişimi, burst rate limit, kapalı provider, restart ve sistemin backup akışını bildirim arızasında durdurmaması.

Başlangıç incelemesi: repo'da çalışan bir notification modülü yoktu (yalnız `src/modules/notifications/obj` artığı); `AuditEvent` yönetim değişikliği kaydıdır, outbox değildir. Olay kaynakları zaten vardı ve **yeniden tanımlanmadı**: başarısızlık `BackupRun` durumları, agent liveness `AgentConnectivityOptions.OfflineTimeout`, kota `QuotaState`, replica sağlığı `BackupReplicationEvaluator`. Bu yüzden dar yeni modül kuruldu.

Sıra (N1 sonrası, hepsi **Planned**):

1. Teams / Slack webhook adaptörleri
2. Telegram
3. Push bildirimleri

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

### N1 — Uygulama kaydı, kararlar ve kanıt (**Completed — 2026-10-14, `feature/n1-notifications`**)

**Kanal bazlı durum (birbirinden ayrı):**

| Kanal | Uygulama | Doğrulama | Not |
|---|---|---|---|
| Email (SMTP) | Implemented | **PASS** — izole SMTP sunucusuna gerçek soket (düz, implicit TLS, STARTTLS + AUTH; 4xx/5xx; kapalı relay; private adres reddi) ve PostgreSQL entegrasyonu; tarayıcıdan gerçek stack'te gönderim | SMTP sunucusu/kimlik **platform** ayarıdır (PlatformAdmin); alıcılar tenant'a aittir. |
| Webhook (HTTPS) | Implemented | **PASS** — izole HTTPS sunucusuna gerçek istek (bearer, HMAC, idempotency, 4xx/5xx/429, redirect, SSRF, proxy bypass) ve PostgreSQL entegrasyonu | Endpoint ve kimlik doğrulama tenant'a aittir. |
| SMS | Implemented | **`IMPLEMENTED — NOT VERIFIED`** | Kullanıcı kararıyla SMS'e özel unit/integration/gerçek gönderim testi **yazılmadı ve çalıştırılmadı**; hiçbir gerçek sağlayıcıya karşı denenmedi. Ortak altyapı testleri (durable outbox, claim/lease, retry, burst, tenant izolasyonu, DI/worker kompozisyonu) SMS'i yalnız dolaylı kapsar. PASS veya production-ready denmez. |

**SMS entegrasyon sınırları:** Tek adaptör `generic-http`: yapılandırılan HTTPS uç noktasına `{"to","text","from"?,"messageId"}` JSON `POST` eder, herhangi bir 2xx'i "kabul edildi" sayar; kimlik doğrulama none/bearer/basic/tek özel header. **Hiçbir satıcının (Twilio, Vonage, operatör SMPP vb.) API'siyle eşleşmez**; farklı istek şekli gereken satıcı için `ISmsProvider` adaptörü yazılmalı ve gerçek hesapla doğrulanmalıdır. Gerçek sağlayıcı kimliği/credential'ı uydurulmadı. Telefon numarası E.164 (`+905551112233`) olarak doğrulanır, API/UI'da maskeli gösterilir; SMS metni tek satır ve ≤160 karakterdir (uzun adlar kısaltılır).

**Mimari kararlar:**

1. **Modül sınırı:** `BackupPlatform.Notifications` (`src/modules/notifications`) yalnız Domain, Application ve SecretProviders.Abstractions'a bağlıdır; backup/restore, agent, storage, ManagedService ve provider projeleri bildirimi bilmez, bildirim modülü de onları bilmez (`NotificationsBoundaryTests`). Gerçekleri okuyan detector'lar altyapıdadır (`PostgreSqlNotificationObservationSource`) ve **yalnız okur**: backup orchestration'a bildirim çağrısı gömülmedi, bu yüzden SMTP/webhook/SMS arızasının backup/restore'a ulaşan bir kod yolu yoktur.
2. **Durable outbox:** `notification_events` + `notification_deliveries` + `notification_conditions` tek per-tenant transaction'da (`pg_advisory_xact_lock`) yazılır; event dedup anahtarı tenant başına unique, delivery (event, kanal) unique, delivery→event bileşik FK tenant'ı taşır. Aynı olguyu gören her sweep/node no-op olur.
3. **Durum makinesi (`NotificationCondition`):** Raise → (escalation / reminder) → Recover; flap cooldown (15 dk), recovery yalnız bildirilmiş condition için; detector bir tür için eksik küme dönerse (okuma hatası) **recovery sayılmaz**. Tek-seferlik olay (başarısız run) yalnız politika açıldıktan sonrakiler için bildirilir (`ActiveSince`), bu yüzden politika açmak geçmişi yağdırmaz.
4. **Delivery worker:** `FOR UPDATE SKIP LOCKED` ile lease'li claim, `(owner, attempt)` ile fenced tamamlama, crash sonrası lease devri, son denemede ölen worker için `attempts_exhausted`, exponential backoff + ±%20 jitter + `Retry-After` (tavan 1 saat), varsayılan 6 deneme. Gönderim **at-least-once**'tır; webhook/SMS `Idempotency-Key`, email `Message-Id` delivery id'sinden türetilir.
5. **Burst/rate/concurrency:** tenant politikası saatlik tavan (varsayılan 30, üst sınır 200) aşılırsa event kaydedilir ama delivery `Suppressed/burst_limited` olur (geçmişte görünür); process başına `MaxConcurrentDeliveries` (4) ve batch; kanal başına 5 test/saat; tenant başına 20 kanal; send timeout lease'in 1/3'ünü aşamaz (başlangıçta doğrulanır).
6. **SSRF/ağ (`NotificationNetworkGuard`):** webhook, SMTP relay ve SMS gateway aynı kapıdan geçer: DNS cevaplarının **tümü** public olmalı, bağlantı doğrulanan IP'ye yapılır (DNS rebinding yok), IPv4-mapped/NAT64/6to4 gömülü adresler açılır, yalnız `https`, URL'de userinfo/query/fragment yok, redirect **izlenmez**, ortam proxy'si **kullanılmaz**, yanıt gövdesi okunmaz. Operatör `Notifications:AllowedPrivateNetworks` CIDR'leriyle istisna verir (varsayılan boş).
7. **Secret:** SMTP parolası, webhook bearer/HMAC anahtarı, SMS credential'ı `ISecretStore` (Data Protection) içindedir; yeni secret satırdan önce yazılır, eski secret commit sonrası silinir. API/audit/log/UI'ya değer çıkmaz; webhook URL'si yalnız `https://host/…` olarak maskeli gösterilir (path token olabilir). SMTP: STARTTLS zorunlu (downgrade yok), `none` yalnız kimlik bilgisi yokken; sertifika doğrulaması platform varsayılanı.
8. **API/yetki:** `/api/tenants/{id}/notifications/*` yalnız TenantAdministrator; `/api/platform/notification-transports/*` yalnız PlatformAdmin; her tenant isteği üye tenant'a bağlanır (başka tenant'ın kanal/id'si 404). Her değişiklik aynı transaction'da audit'lenir; audit adres/URL/secret taşımaz.
9. **Mesajlar:** tür, önem, görünen ad (tek satıra indirilir), birkaç skaler fact (başarısızlık kodu, saat/dakika, yüzde, replica sayıları). Yol, host, veritabanı adı, provider çıktısı **yok**.
10. **Kapsam sınırları (bilinçli):** Teams/Slack/Telegram/push yok; **e-posta** alıcıları onay ister ve kendi kendine unsubscribe edebilir (güvenlik incelemesi, aşağıda); SMS alıcı sahiplik doğrulaması yok.

**Değişen modüller:** `src/core/domain/Notifications`, `src/modules/notifications`, `src/core/infrastructure/Notifications` + EF yapılandırması + migration `NotificationsN1` (yalnız ekleyici, 6 tablo), `src/hosts/api` (endpoint, iki hosted worker, DI, `appsettings.json > Notifications`), `Directory.Packages.props` (MailKit 4.18.0), frontend `features/notifications`, `features/platform/notifications`, tenant ve platform sayfaları.

**Doğrulama (son kod, çalıştırılan):** Release build `-warnaserror` temiz; unit **3879** (N1: 109), architecture **151** (N1: 28), Fast Agent **375**, frontend **610** (typecheck, lint [yalnız önceden var olan 2 uyarı], build PASS). Odaklı PostgreSQL (N1 notification integration **54**: tenant/kanal/politika/audit, claim/lease/fencing, dedup ve eşzamanlı sweep, detector'lar, gerçek SMTP + HTTPS teslimi, outage/restart/backoff, burst, hosted worker kompozisyonu, API yetki/izolasyon/secret sızıntısı) + komşu mevcut suite'ler (fixture isolation, persistence, UploadBandwidth, Audit, StorageConnection migration, AgentConnectivity, BackupOrchestration/Retry/TransferLease/ConcurrencyLimit, PlatformAdministration) **301/301 PASS**. Migration: pending-model temiz, `Down` → `Up` turu, idempotent script boş veritabanına uygulandı. Gerçek stack smoke (tarayıcı + gerçek API + PostgreSQL + gerçek SMTP alıcısı): platform SMTP ayarı → tenant kanalı → test gönderimi → `Gönderildi` geçmişi ve alınan ileti PASS. **Tam PostgreSQL suite'i (1.580 test) çalıştırılmadı; önceki NOT VERIFIED kaydı aynen geçerlidir.** A1/2A `NOT RUN` kalemleri değişmedi.

**Güvenlik incelemesi (2026-10-14, `feature/n1-notifications`, kod düzeltmeleri + testler):**

- **Alıcı onayı (e-posta):** yeni/yeniden hedeflenen/yeniden etkinleştirilen e-posta kanalı `RecipientVerifiedAt = null` ile başlar. Onaysız adrese yalnız **sabit metinli** bir onay isteği (kanal/tenant adı içermez, tenant'ın serbest metni gidemez) gönderilir; uyarı fan-out'u (`NotificationEngine`) ve delivery claim'i (`recipient_unverified`, `Suppressed`) onaysız adresi atlar. Onaylanmamış tek e-posta kanalı varken condition "bildirildi" sayılmaz; adres onaylayınca uyarı kaybolmadan gider. Onay isteği ve testler tenant başına saatlik `TestNotificationsPerTenantPerHour` (20) + kanal başına (5) ile sınırlıdır; sayım tenant kilidi altında ve silinen kanalların teslimatları üzerinden yapıldığından kanal silip yeniden oluşturarak aşılamaz.
- **Kendi kendine unsubscribe:** her e-posta imzalı bir bağlantı (`/api/notifications/recipient?token=`) ve RFC 8058 `List-Unsubscribe` + `List-Unsubscribe-Post: List-Unsubscribe=One-Click` başlığı taşır. Token Data Protection ile mühürlüdür, saklanmaz, tek kanal + tek eylem + **gönderildiği adres parmak izi** taşır (kanal başka adrese taşındıysa eski adrese giden bağlantı çalışmaz). GET yalnız ne yapacağını gösterir (posta tarayıcıları durum değiştirmez), değişiklik POST'ta olur; çerez/oturum yoktur. Unsubscribe kanalı devre dışı bırakır ve onayı siler; yönetici yeniden etkinleştirince adres yeniden onay ister. Audit: `notification_channel.recipient_confirmed/unsubscribed` (actor `System/Anonymous`, adres yok). Anonim uç nokta `DisableAntiforgery` ile çerezsiz işaretlenir; `ApiAntiforgeryMiddleware` bu meta veriyi artık dikkate alır.
  - **N1.1 ile güncellendi:** bağlantı biçimi artık `…/recipient/{confirm|unsubscribe}#token=…` (token fragment'ta); one-click başlığı `…/recipient/unsubscribe?token=…`. GET statik sayfa döner, POST token'ı gövdeden veya sorgudan okur (N1.1 bölümü).
- **Port politikası / SSRF:** webhook ve SMS gateway yalnız 443 (veya `AdditionalHttpsPorts`) kullanabilir; kanal oluşturma/güncelleme `port_not_allowed` ile reddeder, bağlantı anında da `ConnectCallback` aynı kuralı uygular (konfigürasyon sonradan değişse bile). Yasak adres listesi `192.88.99.0/24`, `64:ff9b:1::/48`, `3fff::/20`, `5f00::/16` ile genişletildi. DNS rebinding (doğrulanan IP'ye bağlanma), redirect, proxy, IPv4-gömülü IPv6 incelendi: yeni açık bulunmadı.
- **Secret yönetimi:** SMTP parolası/webhook anahtarı/SMS credential'ı incelendi; değer log/audit/API/UI'ya çıkmıyor, yeni secret satırdan önce yazılıp eskisi commit sonrası siliniyor. Değişiklik gerekmedi.
- **Detector/state machine/concurrency:** durum makinesi, dedup anahtarı (tenant başına unique), tenant advisory lock, `SKIP LOCKED` claim, fenced tamamlama, lease devri, son denemede `attempts_exhausted`, backoff/jitter ve burst sayımı gözden geçirildi; yarış veya veri kaybı hatası bulunmadı. İncelemede bulunan iki sınırdan biri (iptal edilmiş agent/devre dışı iş için yanıltıcı "iyileşti" mesajı) **N1.1'de düzeltildi**; diğeri (iki node'un eski/yeni gözlem kümelerini farklı sırayla uygulaması) flap cooldown ile sınırlı kalır.
- **Migration `NotificationRecipientConfirmation`:** yalnız ekleyici (`notification_channels.RecipientVerifiedAt` nullable + `Kind = 'Email'` check); mevcut satırlar geçerli kalır, veri silinmez. Dev veritabanına uygulanmadı. `has-pending-model-changes` temiz.
- **Çalıştırılan (bu inceleme):** Release solution build `-warnaserror` temiz; unit **3903** (N1: 133), architecture **151**, frontend **611** (typecheck, build PASS, lint yalnız önceden var olan 2 uyarı); odaklı PostgreSQL N1 notification integration **64** (10 yeni: onay isteği, onaysız adresin atlanması ve onayda gönderim, unsubscribe + yeniden etkinleştirme, eski adres/başka tenant/silinmiş kanal bağlantısı, tenant hız sınırı, claim kapısı, port politikası; hosted worker testi gerçek API host'u, gerçek Data Protection token'ları ve gerçek SMTP alıcısıyla onay → uyarı → one-click unsubscribe zincirini çalıştırır) + komşu suite'ler (fixture isolation, persistence, GoogleDrive reliability migration, PlatformAdministration, Audit) **157/157 PASS**. **Tam PostgreSQL suite'i (1.580 test) çalıştırılmadı; önceki NOT VERIFIED kaydı aynen geçerlidir.** SMS'e özel test yazılmadı/çalıştırılmadı. Tarayıcıdan UI duman testi bu incelemede yapılmadı (yeni rozet/buton yalnız Vitest ile kapsandı).

**N1.1 — Son bildirim güvenlik düzeltmeleri (2026-10-14, aynı PR/branch):**

- **Anonim alıcı uç noktalarında hız sınırı.** `RecipientLinkThrottle`: (a) **geçerli token başına** pencere başına 10 istek (tekrar oynatma/DB yükü sınırı; tek istek yapan posta sağlayıcısının one-click unsubscribe'üne dokunmaz); (b) **geçersiz/eksik/süresi geçmiş token'lı istekler istemci başına** 20/dk, aşılırsa `429` + `Retry-After`. Geçerli token istemci adresiyle **hiç** sınırlanmaz: bir saldırganın aynı proxy adresinden yaptığı selin gerçek unsubscribe'ü engellememesi test edildi. Ayarlar `Notifications:RecipientLinkWindow/AttemptsPerToken/InvalidPerClient` (başlangıçta doğrulanır). Yollar `/recipient/{confirm|unsubscribe}`; token'ın eylemi yolla eşleşmezse reddedilir; istek gövdesi 4 KB ile sınırlıdır.
- **Token sızıntısı.** (1) Kişinin açtığı e-posta bağlantıları token'ı **URL fragment**'ında taşır (`…/recipient/confirm#token=…`): tarayıcı fragment'ı göndermez, bu yüzden proxy/CDN/API erişim günlüklerine hiç girmez. GET statik bir sayfa döner (token yok; posta tarayıcıları script çalıştırmaz, durum değişmez); sayfanın tek script'i (CSP `script-src 'sha256-…'`, `form-action 'self'`, `Referrer-Policy: no-referrer`, `no-store`) token'ı fragment'tan form gövdesine taşır ve `history.replaceState` ile adresi temizler. (2) RFC 8058 one-click için `List-Unsubscribe` URL'si token'ı sorguda taşımak **zorundadır**; bu token yalnız unsubscribe yapar (posta başlatamaz/onaylayamaz). (3) API günlükleri: `Microsoft.AspNetCore.Hosting.Diagnostics` kodda `Warning`'e sabitlenir (istek satırı sorguyu içerir); regresyon testi `Trace` düzeyinde tüm ASP.NET Core günlüklerini yakalayıp token'ın hiçbirinde bulunmadığını doğrular (sabitleme kaldırılınca test kırılır — mutasyonla doğrulandı). (4) Proxy: Traefik v3 `accessLog.fields.queryParameters.defaultMode: drop` (sürümü desteklemiyorsa `fields.names.RequestPath: drop` veya yol bazlı `observability.accessLogs: false`), nginx `$uri`, CDN/WAF/APM uyarıları `deploy/release/README-backend.txt` içinde belgelendi. Proxy tarafı yapılandırma operatörün sorumluluğudur ve gerçek Traefik'le denenmedi.
- **İdari kapatma ≠ iyileşme.** Bir condition'ın konusu (agent: iptal/devre dışı/silinmiş; iş: devre dışı/silinmiş) hizmet dışıysa condition **sessizce** kapanır, `Recovered` üretilmez. Gerçek iyileşme (agent yeniden görüldü, iş yeniden başarılı, replica sağlığı döndü) aynen bildirilir. `INotificationObservationSource.RetiredSubjectsAsync` detector'larla aynı tanımları kullanır (agent `Active`, iş `Enabled`), tenant'a bağlıdır ve okuma hatasında **fırlatır** (asla "kimse kapatılmadı" diye yanıtlanmaz; sweep tekrarlanır, yanlış iyileşme üretilmez).
- **Çalıştırılan (N1.1):** Release solution build `-warnaserror` (CI bayraklarıyla) temiz; unit **3908** (N1: 138), architecture **151**, Fast Agent **375**, frontend **611** (typecheck, lint [yalnız önceden var olan 2 uyarı], build PASS); odaklı PostgreSQL N1 notification integration **72/72** (8 yeni: invalid-flood sonrası geçerli one-click, token tekrar oynatma sınırı, yol/eylem eşleşmesi, statik sayfa + CSP, Trace düzeyinde log sızıntı testi, iptal edilen agent ve devre dışı iş için sessiz kapanış + gerçek iyileşmenin korunması, `RetiredSubjectsAsync` tenant izolasyonu). **Tam PostgreSQL suite'i (1.580 test) çalıştırılmadı.** SMS'e özel test yazılmadı/çalıştırılmadı; SMS `IMPLEMENTED — NOT VERIFIED` kalır. Tarayıcıda gerçek fragment → script → POST akışı bu dilimde elle denenmedi (HTTP düzeyinde ve sayfa içeriği testle doğrulandı; script mantığı için tarayıcı testi yok: `NOT RUN`).

**Kalan riskler / NOT RUN:**

- **SMS: `IMPLEMENTED — NOT VERIFIED`** (yukarıda). Gerçek SMS sağlayıcısı, vendor adaptörü ve gerçek gönderim `NOT RUN`.
- Gerçek harici SMTP sağlayıcısı (ör. kurumsal relay, STARTTLS sertifika zinciri) ve üçüncü taraf webhook alıcısı `NOT RUN`; TLS testleri izole sunucuların self-signed sertifikasını açık bir test seam'iyle kabul eder (production'da seam yok).
- Alıcı doğrulaması **yalnız e-postada** vardır (güvenlik incelemesi): onaylanmamış adrese yalnız sabit metinli bir onay isteği gider; kalan kötüye kullanım yüzeyi tenant başına saatlik onay/test limitidir (aşağıda). Mevcut (N1'in ilk sürümünden kalma) e-posta kanalları migration sonrası **onaysız** sayılır ve yeniden onay isteyene kadar uyarı almaz — bilinçli güvenli varsayılan.
- Webhook/SMS uç noktası portu: varsayılan **yalnız 443**; ek portlar `Notifications:AdditionalHttpsPorts` ile operatörce eklenir (güvenlik incelemesinde kapatıldı). SMTP relay portu platform yöneticisinin ayarıdır ve bu politikaya tabi değildir.
- Çok node'lu ölçekte sweep tenant başına her dakika ~5 sorgu çalıştırır (kota okuması gerektiğinde projeksiyon hesaplayabilir); büyük tenant sayısında profil gerekir (`NOT RUN`, yük ölçülmedi).
- `replica_degraded` işin **son başarılı** run'ının replica sağlığını değerlendirir; eski artifact'lar kapsam dışıdır. Geçmiş (90 gün) bakım turu saatte bir çalışır.
- Production/staging dağıtımı ve release yapılmadı.
- **SMS alıcıları doğrulanmıyor** (e-postadaki onay/unsubscribe SMS'e uygulanmadı): tenant yöneticisi herhangi bir numaraya SMS aktarabilir. SMS `IMPLEMENTED — NOT VERIFIED` olduğundan ve SMS'e özel test bilinçli yazılmadığından bu açık kapatılmadı; SMS gerçek sağlayıcıyla doğrulanırken alıcı onayı (ör. kısa kodlu doğrulama) ve STOP davranışı birlikte tasarlanmalı.
- Alıcı bağlantıları (N1.1): hız sınırı **vardır** (token başına 10/dk, geçersiz istek başına istemci bütçesi 20/dk) ve geçerli token istemci adresine göre **asla** sınırlanmaz. Kalan: ters proxy arkasında `RemoteIpAddress` proxy adresidir (forwarded headers yapılandırılmadıkça); geçersiz-istek bütçesi bu durumda ortak olur, geçerli unsubscribe'ü etkilemez. **List-Unsubscribe (one-click) URL'sindeki token sorgu dizesinde zorunlu olarak taşınır**; API host'u bunu hiçbir yerde loglamaz, ancak **ters proxy/CDN/WAF/APM erişim günlüklerinin query'yi saklamaması operatör yapılandırmasıdır** (Traefik v3: `accessLog.fields.queryParameters.defaultMode: drop`; ayrıntı `deploy/release/README-backend.txt`) ve `NOT RUN` (gerçek Traefik'e karşı doğrulanmadı). Kişinin açtığı sayfa bağlantısının token'ı URL **fragment**'ındadır ve hiçbir günlükte görünmez. **Alıcı başına küresel limit/suppression listesi yoktur.**
- Onay/unsubscribe bağlantıları `Notifications:PublicBaseUrl` ister; ayarlı değilse hiçbir e-posta gönderilmez (`public_url_not_configured`). Gerçek posta kutusu/MTA ile ve gerçek bir ters proxy arkasında uçtan uca bağlantı tıklaması `NOT RUN` (yalnız izole SMTP + API host'u test edildi).
- **İdari kapatma ile gerçek iyileşme** artık ayrılır (N1.1, aşağıda). Kalan sınır: yalnız `agent_offline`, `backup_stale` ve `replica_degraded` için ayrım vardır; bir agent/iş **yeniden etkinleştirildiğinde** koşul taze bir condition olarak yeniden başlar (flap cooldown'a tabidir). Tenant, plan veya kota değişiklikleriyle biten `quota_critical` gerçek iyileşme sayılır.

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
Faz 2A — Upload bandwidth limiting                               [Completed: çalışma dalı]
        ↓
N1 (Faz 24) — Email/webhook/SMS bildirimleri  [Completed; SMS NOT VERIFIED]  →  R1 (Faz 20) restore testi  [Next]
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
| 1 | **Faz 2A — Upload bandwidth limiting** | Per-agent/tenant/job, bounded stream, paralel transfer ölçümü, relay/SFTP regresyon | **Completed — çalışma dalı, 2026-10-14** (kanıt ve kalan riskler Bölüm 4) |
| 2 | **N1 (Faz 24) — Email + generic webhook (+ SMS) bildirimi** | Durable event/dispatch, dedup, retry, SSRF koruması, tenant policy, CI | **Completed — çalışma dalı, 2026-10-14** (Email/Webhook PASS; **SMS `IMPLEMENTED — NOT VERIFIED`**; kanıt ve riskler Bölüm 26) |
| 2A| **UI/UX Redesign & Storage Hub** | Tam frontend SaaS tasarımı, responsive 375/1440px, Storage sihirbazı ve detay akışı, 64 test dosyası / 622 test PASS, Chromium smoke | **Completed — çalışma dalı, 2026-10-15** |
| 2B| **PostgreSQL Test Suite Optimizasyonu & Profiling** | 1.695 entegrasyon testinin detaylı süre profili çıkarıldı; SeedBatchAsync ile 205 artifact seeding'i 59s'den 14s'ye düşürüldü, UploadPacerTests flaky döngüsü deterministik hale getirildi, BackupTransferLease timeout'ları kısaltıldı, 1.695 test 0 hata (1.695/1695 PASS) ile doğrulandı. | **Completed — çalışma dalı, 2026-10-15** |
| 2C| **MSSQL Windows VM Full Backup & Replace Overwrite E2E** | Windows 11 VM SQL Server 2025 üzerinde NewDatabase ve ReplaceExistingDatabase (confirmOverwrite doğrulamalı) gerçek E2E PASS | **Completed — çalışma dalı, 2026-10-15** |
| 3 | **R1 (Faz 20) — İzole MSSQL restore testi** | Tasarım ve kabul matrisi hazırlandı (`docs/R1_Restore_Verification_Design.md`), DR tatbikatı yapıldı | **Tasarım & DR Tamamlandı** — Otomasyon planlandı |
| 4 | **E1 — Artifact encryption** | Önce tehdit modeli/anahtar yaşam döngüsü, sonra streaming encrypt/decrypt + restore | Planned — güvenlik |
| 5 | **Faz 10–12 — MSSQL Differential / Transaction Log / PITR** | Full/log base chain, LSN, restore ordering, retention-safe pin, MSSQL E2E | Planned — MSSQL ürün değeri |
| 6 | **Faz 3 ve 9 — File/Folder Backup + Restore** | Include/exclude, symlink, partial warnings, manifest, güvenli restore; VSS sonra | Planned — ikinci kaynak |
| 7 | **Faz 2B/2C — Transfer profilleri ve recovery** | Profil/DST, pause/resume, provider gerçek capability ve durable checkpoint | Planned — ihtiyaçla öne alınabilir |
| 8 | **Faz 15/16/17/18 — 3-2-1, S3, Object Lock, SMB/NAS** | Provider-specific safety, encryption/key policy, immutable retention, failure acceptance | Planned |
| 9 | **Faz 4–7/13/21–23/25–30** | VSS/Linux installer/snapshot, Docker, PostgreSQL, monitoring/health/anomaly, updates, advanced security/reports | Future / ihtiyaç bazlı |
| Ayrı | **A1 — Canlı provider & Windows acceptance** | Gerçek Drive OAuth/API ve Windows DPAPI/MSSQL VM kapanışı; senaryo matrisi Bölüm 1B | **Completed — çalışma dalı, 2026-10-14** (`NOT RUN` kalemleri Bölüm 1B'de); deployment değil |
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
