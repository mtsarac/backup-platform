# Genel Backup Yönetim Sistemi — MVP Geliştirme Planı

## 1. Amaç

Bu planın amacı, sistemi gelecekte büyütülebilir ve modüler kalacak şekilde kurarken ilk aşamada çalışan, güvenilir ve üretime yakın bir temel backup platformu ortaya çıkarmaktır. **Bölüm 1–13 ilk MVP'nin tarihsel gereksinimleridir; güncel uygulama durumu Bölüm 14'te tutulur.**

MVP'nin odak noktası:

- gerçek multi-tenant yapı,
- merkezi backend,
- Windows agent,
- Linux desteğine hazır ortak agent mimarisi,
- MSSQL full backup,
- native MSSQL ve Docker üzerinde çalışan MSSQL senaryolarına uygun mimari,
- manuel ve zamanlanmış backup,
- local / merkezi storage,
- backup geçmişi ve durum takibi,
- checksum doğrulama,
- `RESTORE VERIFYONLY`,
- retention,
- audit ve temel operasyonel güvenliktir.

MVP, ileride eklenecek PostgreSQL, Linux agent implementasyonu, gelişmiş restore, multi-storage, immutable storage, VSS/snapshot, S3/Azure ve benzeri özellikler için yeniden mimari yazmayı gerektirmeyecek şekilde hazırlanmalıdır.

---

## 1A. 2026-10-13 Durum Notu

**Repository:** [`mtsarac/backup-platform` / main `c6f66b5c`](https://github.com/mtsarac/backup-platform/tree/c6f66b5c764eef8081a3dd936d1778eda80d1a09). Temel MSSQL Full Backup MVP uygulaması ve post-MVP birçok genişleme `main` içindedir. Hardening PR #12 ve Accessible UI PR #5 birleştirilmiştir; güncel main Fast CI PASS. Bu durum tüm gerçek Windows SQL Server ve canlı Google Drive kabul testlerinin yapıldığı anlamına gelmez.

İlk MVP kapsamının dışında tanımlanan MSSQL Full Restore (UI dahil), SFTP, Google Drive, canonical multi-destination, quota, managed service, download ve support access kodda mevcut olduğundan eski “henüz yok” ifadeleri **Bölüm 11'de tarihsel kapsam sınırı** olarak okunmalıdır. A0 ve A0.1 kritik audit ve durable orphan ledger kapanışı 2026-10-14'te çalışma dalında kapatıldı (Bölüm 10); tüm 35 kriterin tek bir gerçek Windows MSSQL E2E acceptance koşusunda ayrı ayrı imzalanması ayrıca değerlendirilmelidir.

**Ürün modeli:** Yüz yüze/doğrudan satış; PlatformAdmin tarafından elle plan/abonelik atama. **Online checkout, payment gateway, billing provider ve uygulama içi faturalama kapsam dışı**; mevcut `Plan`/`TenantSubscription`/quota teknik kullanım için korunur.

**Sıradaki işlemler:** A0 kritik audit, A1 canlı provider/Windows acceptance ve roadmap **Faz 2A — Upload Bandwidth Limiting** ve sertleştirme/canlı kabulü (**2A.1**: relay'de backend-tarafı pacing, kısa ömürlü transfer lease'i, Windows→Drive paced/paralel/iptal/retry kabulü) ve nihai inceleme (upload başına slice, lease fence/pacer sertleştirmesi, DB saatli grant, upload-only hız ölçümü) çalışma dalında tamamlandı (Faz 2B/2C başlanmadı). **N1 bildirimleri 2026-10-14'te çalışma dalında tamamlandı** (email + webhook doğrulandı; SMS `IMPLEMENTED — NOT VERIFIED`). Sıradaki yüksek öncelikler R1 izole restore testi ve E1 artifact şifreleme. Production/staging dağıtımı bu geliştirme planının kapsamı değildir.

---

## 2. Sabit Mimari Kararlar

### 2.1 Genel prensipler

- Sistem modüler geliştirilecektir.
- Domain katmanı mümkün olduğunca framework bağımsız, pure .NET olacaktır.
- Modüller birbirleriyle doğrudan implementation bağımlılığı üzerinden değil, tanımlı interface/contracts üzerinden haberleşecektir.
- Gereksiz biçimde her şeyi event-driven yapmaya çalışılmayacaktır.
- Gelecekteki modüller düşünülerek extension point'ler baştan tanımlanacaktır.
- Kullanılmayan ileri özellikler MVP'de implemente edilmeyecek; yalnızca gerekli domain ve abstraction desteği hazırlanacaktır.

### 2.2 Backend

- .NET / ASP.NET Core kullanılacaktır.
- Metadata veritabanı PostgreSQL olacaktır.
- Entity Framework Core kullanılabilir ancak Domain katmanı EF Core'a bağımlı olmayacaktır.
- Scheduler için hazır ve olgun bir çözüm kullanılacaktır: Quartz.NET.
- Quartz.NET doğrudan Domain'e sızmayacak; scheduler bir application/infrastructure abstraction arkasında tutulacaktır.
- Backend authoritative scheduler olacaktır.
- Agent kendi başına bağımsız scheduler gibi davranmayacaktır.
- Agent offline iken kaçırılan job'lar için job bazlı configurable grace period bulunacaktır.
- Grace period içindeki son uygun çalışma tekrar tetiklenebilecektir.
- Aynı job için yeni zaman geldiğinde mevcut run hâlâ devam ediyorsa davranış job bazlı `Skip` veya `Queue` olarak seçilebilecektir.
- Varsayılan davranış `Skip` olmalıdır.

### 2.3 Agent

Ortak yapı:

```text
Agent.Core
Agent.Platform.Windows
Agent.Platform.Linux
```

MVP'de Windows Agent aktif geliştirilecektir.

Linux Agent'ın tamamı MVP zorunluluğu değildir ancak:

- Agent.Core Linux'tan bağımsız çalışabilmelidir.
- OS-specific kod Agent.Core içine yayılmamalıdır.
- Platform özellikleri interface üzerinden ayrılmalıdır.
- Linux implementasyonu geldiğinde mevcut backup orchestration katmanı yeniden yazılmamalıdır.

### 2.4 Agent-backend iletişimi

Hybrid iletişim modeli kullanılacaktır:

- bağlantı mevcutsa backend push,
- bağlantı yoksa veya push kanalı çalışmıyorsa polling fallback.

Protokol ayrı ve versioned contracts üzerinden tanımlanacaktır.

Örnek modül:

```text
BackupPlatform.Contracts
```

Bu paket:

- backend internal DTO'larının kopyası olmayacaktır,
- agent-backend wire protocol'ünü temsil edecektir,
- protocol/API version bilgisi taşıyacaktır,
- belirli bir compatibility window destekleyecektir.

### 2.5 Multi-tenant

MVP ilk günden gerçek multi-tenant olacaktır.

Temel model:

```text
User
Tenant
TenantMembership
Role
```

- Kullanıcı birden fazla tenant'a üye olabilecektir.
- Roller global kullanıcıya değil TenantMembership'e bağlanacaktır.
- PlatformAdmin tenant rollerinden ayrı olacaktır.
- Tenant-owned bütün kritik entity'lerde `TenantId` olacaktır.
- Tenant isolation repository/query/application katmanlarında merkezi şekilde enforce edilmelidir.
- Tenant sızıntısını önlemek için integration testleri yazılmalıdır.

### 2.6 Agent enrollment

Enrollment akışı:

```text
Platform Admin
    ↓
Tenant için enrollment izni
    ↓
Tenant Admin
    ↓
Tek kullanımlık enrollment token
    ↓
Agent
    ↓
Permanent agent identity / credential
```

Desteklenmesi gereken lifecycle:

- create enrollment token,
- expire token,
- single-use tüketim,
- agent credential rotate,
- agent revoke,
- re-enroll,
- disabled agent.

İlk sürümde unique agent secret kullanılabilir.

Mimari:

- client certificate,
- mTLS

geçişine uygun tasarlanmalıdır.

### 2.7 Secrets

Secret management provider abstraction üzerinden yapılacaktır.

İlk implementasyon:

- ASP.NET Core Data Protection,
- persistent key ring,
- production deployment'ta key-at-rest protection.

Daha sonra:

- Vault,
- Azure Key Vault,
- başka harici secret manager

eklenebilmelidir.

Agent local credential saklama için:

```text
IAgentSecretStore
```

benzeri platform abstraction kullanılmalıdır.

Windows ve Linux ayrı güvenli storage implementasyonlarına sahip olabilir.

Storage credential'ları agent'ta kalıcı tutulmayacaktır.

---

## 3. Ana Domain Modeli

Basit `BackupJob + Backup` modeli kullanılmayacaktır.

Temel domain ayrımı:

```text
Tenant
User
TenantMembership

Agent
AgentCredential
AgentCapability

BackupJob
BackupSchedule
BackupRun
BackupRunAttempt
BackupArtifact
BackupReplica

StorageProvider
StorageCredential

SqlInstance
SqlDatabase

AuditEvent
SystemLog
NotificationEvent
```

### 3.1 BackupJob

Kalıcı backup tanımıdır.

İçermesi beklenen başlıklar:

- tenant,
- agent,
- source type,
- source configuration,
- schedule,
- storage policy,
- retention policy,
- retry policy,
- compression policy,
- encryption policy,
- overlap policy (`Skip` / `Queue`),
- offline grace period,
- enabled state.

### 3.2 BackupRun

Bir job'ın belirli bir zamanda çalıştırılmasını temsil eder.

Run:

- manual,
- scheduled,
- missed schedule recovery

gibi tetik türlerini ayırt edebilmelidir.

### 3.3 BackupRunAttempt

Retry yeni BackupRun üretmeyecektir.

```text
BackupRun
├── Attempt 1
├── Attempt 2
└── Attempt 3
```

Her attempt:

- başlangıç/bitiş,
- durum,
- hata,
- kullanılan agent,
- progress,
- cancellation bilgisi

gibi kendi execution metadata'sını tutmalıdır.

### 3.4 BackupArtifact

Oluşturulan mantıksal backup çıktısını temsil eder.

Artifact, storage location ile aynı kavram değildir.

Örnek:

```text
ERP_20261001_FULL.bak
```

### 3.5 BackupReplica

Artifact'ın bir storage üzerindeki fiziksel kopyasını temsil eder.

MVP'de tek replica kullanılabilir.

Domain baştan:

```text
BackupArtifact
    ├── BackupReplica
    ├── BackupReplica
    └── BackupReplica
```

yapısını desteklemelidir.

Bu sayede gelecekte multi-storage migration gerektirmez.

---

## 4. Modül Sınırları

Önerilen logical modüller:

```text
Platform.Domain
Platform.Application
Platform.Contracts
Platform.Infrastructure

Platform.Identity
Platform.Tenancy
Platform.Agents
Platform.Scheduling
Platform.Backups
Platform.Storage
Platform.Audit
Platform.Notifications

Agent.Core
Agent.Platform.Windows
Agent.Platform.Linux

BackupProviders.Abstractions
BackupProviders.MSSQL
BackupProviders.FileSystem

StorageProviders.Abstractions
StorageProviders.Local
StorageProviders.Central

SecretProviders.Abstractions
SecretProviders.DataProtection

SnapshotProviders.Abstractions
```

MVP'de bütün modüllerin ayrı deployable servis olması gerekmez.

Ama logical sınırlar korunmalıdır.

---

## 5. Capability Sistemi

Tek tek hardcoded feature boolean'ları farklı katmanlara dağılmamalıdır.

Ortak capability modeli oluşturulmalıdır.

Capability örnekleri:

### Agent

- OperatingSystem
- SupportsDockerDiscovery
- SupportsSnapshot
- SupportsBandwidthControl

### MSSQL

- SupportsFullBackup
- SupportsDifferentialBackup
- SupportsLogBackup
- SupportsCompression
- SupportsChecksum
- SupportsCopyOnly
- SupportsBackupEncryption
- SupportsTDE

### Storage

- SupportsResume
- SupportsMultipart
- SupportsAtomicMove
- SupportsCapacityQuery
- SupportsImmutableStorage
- SupportsServerSideChecksum
- SupportsDelete

Capability'ler version/constraint bilgisi taşıyabilmelidir.

---

# 6. Geliştirme Fazları

## Phase 0 — Foundation ve sınırlar

Bu fazda ürün özelliği geliştirmekten çok geri kalan sistemin üzerine kurulacağı temel hazırlanır.

### Yapılacak işler

- Solution/project sınırlarının oluşturulması.
- Domain ve Application katmanlarının Infrastructure'dan ayrılması.
- Contracts paketinin oluşturulması.
- Protocol versioning yaklaşımının tanımlanması.
- Tenant modelinin oluşturulması.
- TenantMembership ve tenant role modelinin oluşturulması.
- PlatformAdmin modelinin oluşturulması.
- Tenant isolation mekanizmasının oluşturulması.
- Audit event altyapısının oluşturulması.
- Secret provider abstraction'ın oluşturulması.
- Data Protection implementasyonunun oluşturulması.
- Capability modelinin oluşturulması.
- Scheduler abstraction'ın oluşturulması.
- Quartz.NET infrastructure implementasyonunun hazırlanması.
- BackupJob / BackupRun / BackupRunAttempt / BackupArtifact / BackupReplica domain modelinin oluşturulması.
- Modül bağımlılık kurallarının testlerle enforce edilmesi.
- Temel migration altyapısının kurulması.

### Bu fazın çıktısı

Sistem henüz gerçek backup almayabilir.

Ancak sonraki fazlarda kullanılacak domain sınırları değişmeden kalabilecek durumda olmalıdır.

---

## Phase 1 — Agent identity, enrollment ve bağlantı

### Backend

- Tenant bazlı agent enrollment yetkilendirmesi.
- Tek kullanımlık enrollment token.
- Enrollment token expiration.
- Agent registration.
- Agent credential oluşturma.
- Agent revoke.
- Agent credential rotation altyapısı.
- Agent listesi.
- Agent detail.
- Agent version.
- Agent protocol version.
- Agent capabilities.

### Agent

- Windows Service host.
- Agent identity oluşturma.
- Enrollment akışı.
- Agent credential secure local storage.
- Backend authentication.
- Heartbeat.
- Push channel bağlantısı.
- Polling fallback.
- Reconnect logic.
- Capability reporting.

### Durumlar

Minimum:

```text
Online
Offline
Disabled
Revoked
VersionMismatch
```

### Acceptance

- Bir tenant admin yetkili olduğu tenant için agent enroll edebilmeli.
- Başka tenant'ın agent'ını görememeli.
- Agent restart sonrası kimliğini korumalı.
- Credential revoke edildiğinde bağlantı reddedilmeli.
- Agent offline/online durumu güvenilir şekilde izlenebilmeli.

---

## Phase 2 — MSSQL discovery ve compatibility

MSSQL ilk database backup provider olacaktır.

Provider:

```text
IDatabaseBackupProvider
```

benzeri ortak contract üzerinden sisteme bağlanmalıdır.

MSSQL implementation'ın kendi iç sınırları bulunmalıdır:

```text
Discovery
Capability Detection
Compatibility
Command Building
Execution
Verification
Metadata
```

### Yapılacak işler

- SQL instance connection modeli.
- Windows ve SQL authentication desteği.
- SQL Server version discovery.
- Edition discovery.
- Engine edition discovery.
- Database discovery.
- Database state discovery.
- Recovery model discovery.
- Compatibility level discovery.
- Capability detection.
- Permission pre-check.
- SQL backup staging path validation.
- SQL Server service account'un staging path'e yazabilmesine yönelik pre-check veya doğrulama mekanizması.
- Kaynak SQL metadata'sının backend'e iletilmesi.

### MVP sınırı

Bu fazda:

- Full Backup hazırlanacaktır.
- Differential henüz yok.
- Transaction Log backup henüz yok.
- Point-in-Time Restore yok.

---

## Phase 3 — İlk uçtan uca MSSQL backup

İlk gerçek vertical slice.

Akış:

```text
User
  ↓
Manual Backup
  ↓
Backend creates BackupRun
  ↓
Agent receives Run
  ↓
Pre-check
  ↓
MSSQL Full Backup
  ↓
RESTORE VERIFYONLY
  ↓
Source Checksum
  ↓
Artifact preparation
  ↓
Artifact Checksum
  ↓
Local/Central Storage
  ↓
Replica verification
  ↓
Metadata
  ↓
Completed
```

### MSSQL

- Full backup.
- COPY_ONLY opsiyonu.
- CHECKSUM capability kontrolü.
- Compression capability kontrolü.
- Dinamik backup command builder.
- `RESTORE VERIFYONLY`.

### Checksum

İki ayrı checksum tutulacaktır:

```text
SourceChecksum
ArtifactChecksum
```

`SourceChecksum` backup engine tarafından üretilen kaynak backup dosyasını temsil eder.

`ArtifactChecksum` storage'a gönderilen son artifact'ı temsil eder.

### Staging

- SQL Server'ın erişebildiği local staging alanı kullanılacaktır.
- Yeterli disk alanı pre-check yapılacaktır.
- Storage'a doğrudan SQL Server backup yazılması temel model olmayacaktır.
- Upload + remote verification tamamlanmadan staging dosyası silinmeyecektir.
- Başarılı verification sonrası cleanup yapılacaktır.

### Storage

İlk storage implementasyonları:

- Local storage.
- Central server storage.

Storage abstraction capability-driven olacaktır.

### Acceptance

- Kullanıcı manuel MSSQL Full Backup başlatabilmeli.
- Run ve Attempt ayrı izlenebilmeli.
- Backup SQL tarafından doğrulanabilmeli.
- İki checksum kaydedilebilmeli.
- Artifact storage'a aktarılabilmeli.
- Replica metadata kaydedilmeli.
- İşlem başarısız olursa anlamlı hata gösterilmeli.

---

## Phase 4 — Scheduling ve idempotency

### Yapılacak işler

- BackupSchedule modeli.
- Quartz.NET entegrasyonu.
- Backend authoritative scheduling.
- Run idempotency.
- Duplicate trigger engelleme.
- Agent offline davranışı.
- Job-specific grace period.
- Missed run recovery.
- Job overlap policy:
  - Skip,
  - Queue.
- Next run calculation.
- Scheduler restart recovery.
- Backend restart sonrası schedule recovery.

### Acceptance

- Aynı schedule event'i iki BackupRun üretmemeli.
- Backend restart edilse dahi schedule kaybolmamalı.
- Agent grace period içinde geri dönerse ilgili missed run politikaya göre çalıştırılabilmeli.
- Çalışan job varken yeni run için `Skip / Queue` davranışı uygulanmalı.

---

## Phase 5 — Retry, cancellation ve execution control

### Retry

Retry aynı BackupRun altında yeni Attempt oluşturacaktır.

Destek:

- configurable max attempts,
- configurable backoff,
- retryable / non-retryable error ayrımı.

### Cancellation

Cancellation stage-aware olacaktır.

Her pipeline aşaması kendi güvenli cancellation davranışını tanımlamalıdır.

Örneğin:

- waiting → anında cancel,
- upload → güvenli multipart abort/cleanup,
- compression → cooperative cancellation,
- SQL backup → SQL operasyonunun güvenli biçimde durdurulması için provider davranışı.

### Concurrency

Aşağıdaki limit seviyeleri desteklenecektir:

- global,
- tenant,
- agent.

Agent concurrency configurable olacaktır.

Default:

```text
Agent Concurrent Backup Limit = 1
```

### Acceptance

- Retry attempt geçmişi kaybolmamalı.
- Non-retryable hata tekrar denenmemeli.
- Cancel sonrası staging/temp cleanup güvenli yapılmalı.
- Concurrency limitleri tenant izolasyonunu bozmamalı.

---

## Phase 6 — Retention

Retention kararının sahibi backend olacaktır.

Storage işlemi orchestrate edilecektir.

İlk politikalar:

- son N backup,
- N gün.

Silme iki aşamalı olmalıdır:

```text
Logical / Soft Delete
        ↓
Grace Period
        ↓
Physical Delete
```

Varsayılan grace period configurable olmalıdır.

### Yapılacak işler

- RetentionPolicy modeli.
- Retention evaluation.
- Soft-delete.
- Physical delete queue.
- Audit.
- Failed deletion retry.
- Replica-aware delete tasarımı.

MVP tek replica kullansa bile retention Artifact ve Replica ayrımını bozmayacaktır.

---

## Phase 7 — Temel web panel

Minimum sayfalar:

### Authentication

- Login.
- Tenant selection.
- Kullanıcının tenant üyelikleri.

### Dashboard

- online agent sayısı,
- offline agent sayısı,
- aktif backup job,
- son backup sonuçları,
- failed backup sayısı.

### Agents

- liste,
- detail,
- heartbeat,
- OS,
- agent version,
- protocol version,
- capabilities,
- MSSQL instances.

### Backup Jobs

- liste,
- create,
- edit,
- enable/disable,
- manual run,
- schedule,
- overlap policy,
- offline grace period,
- retention.

### Backup History

- Run listesi,
- Attempt listesi,
- Artifact,
- Replica,
- checksum,
- errors,
- timestamps.

### Audit

- kritik kullanıcı ve sistem işlemleri.

### Notifications

MVP'de yalnızca system/internal notification event'leri; Email/Teams/Slack/Telegram yoktu (tarihsel MVP sınırı). **Post-MVP N1 (2026-10-14, çalışma dalı):** durable outbox'lı email, generic HTTPS webhook ve SMS bildirimleri eklendi (roadmap Bölüm 26). N1 güvenlik incelemesinde e-posta alıcıları için **adres sahibi onayı + kendi kendine unsubscribe** (imzalı bağlantı, `List-Unsubscribe`), webhook/SMS için **yalnız 443 (operatör eklemedikçe) port politikası** ve tenant başına hız sınırı eklendi; operatör `Notifications:PublicBaseUrl` vermeden e-posta gönderilmez. **N1.1:** alıcı bağlantılarına hız sınırı (geçerli one-click unsubscribe sınırlanmaz), token'ın fragment'ta taşınması + log/proxy sızıntı azaltma ve belgeleme, ve iptal edilen agent/devre dışı iş için yanlış "Recovered" bildiriminin kaldırılması eklendi. SMS `IMPLEMENTED — NOT VERIFIED` kalır (alıcı doğrulaması yok). Teams/Slack/Telegram/push hâlâ yok.

---

# 7. Docker Uyumuna Hazırlık

MVP'nin temel MSSQL backup akışı Docker'a özel olmamalıdır.

Prensip:

```text
Database Engine != Runtime
```

Abstraction:

```text
IDatabaseBackupProvider
IDatabaseRuntime
```

MSSQL backup provider normal SQL bağlantısıyla çalışmalıdır.

Docker:

- discovery,
- port mapping,
- container metadata,
- runtime information

için kullanılmalıdır.

Backup işleminin temel yolu `docker exec` olmamalıdır.

### Güvenlik

Agent'a doğrudan yüksek yetkili Docker socket erişimi vermek yerine ileriki Docker fazında ayrı ve dar yetkili bir privileged helper tasarlanacaktır.

MVP'de contracts/extension point hazırlanabilir; helper implementasyonu Docker fazında yapılacaktır.

---

# 8. File/Folder Backup MVP Sınırı

FileSystem provider mimaride bulunabilir ancak Windows file/folder backup MVP'nin MSSQL vertical slice'ından sonra ele alınmalıdır.

İlk sürüm geldiğinde:

- normal file/folder enumeration,
- exclusions,
- compression,
- locked/open file error handling

desteklenecektir.

MVP FileSystem sürümünde VSS zorunlu değildir.

Locked dosyalar sessizce atlanmamalıdır.

Sonuç:

- warning,
- partial failure,
- failure

olarak açıkça raporlanmalıdır.

Snapshot abstraction şimdiden tanımlanabilir:

```text
ISnapshotProvider
```

Ancak VSS implementation sonraki roadmap'de olacaktır.

---

# 9. Güvenlik Gereksinimleri

MVP tamamlanmış sayılmadan:

- HTTPS zorunlu olmalı.
- Tenant isolation enforce edilmeli.
- Agent authentication zorunlu olmalı.
- Enrollment token single-use olmalı.
- Secrets plaintext saklanmamalı.
- Password hashing güvenli algoritma ile yapılmalı.
- RBAC tenant membership üzerinden uygulanmalı.
- PlatformAdmin ayrı tutulmalı.
- Audit log append-only olmalı.
- Uygulama API'sinden audit update/delete yapılamamalı.
- Backup delete audit edilmelidir.
- Restore/verify operasyonları audit edilmelidir.
- Storage path'leri tenant izolasyonuna uygun olmalıdır.
- Agent storage credential'ını persistent saklamamalıdır.
- API rate limiting uygulanmalıdır.

---

# 10. Audit Log

Audit log append-only olacaktır.

Minimum event'ler:

- login,
- logout,
- tenant membership change,
- role change,
- enrollment token create,
- agent enroll,
- agent revoke,
- agent credential rotate,
- backup job create/edit/delete,
- manual backup run,
- scheduled run,
- backup cancellation,
- retention delete,
- storage configuration change,
- secret change.

Audit kayıtları tenant context ve actor bilgisi taşımalıdır.

Platform işlemleri ayrıca ayırt edilebilmelidir.

**A0 / A0.1 kabul notu — 2026-10-14:** `feat/a0-audit-trail-completion` dalında append-only audit altyapısı genişletildi ve orphan fiziksel silme crash riski `backup_storage_cleanups` tablosu ile kapatıldı. Olay → yazan servis → transaction/idempotency → actor/tenant → test matrisi ve açık sınırlar **`Gelecek_Gelistirmeler_Roadmap_2026-10-13_Revize.md`, Bölüm 1B** içindedir. Kullanıcı istekleri gerçek actor ile, worker/agent işlemleri `System`, reddedilen authentication `Anonymous` olarak ayrılır; scoped audit reads tenant-isolated ve tenant yoksa fail-closed'dur.

Odaklı PostgreSQL A0 + orphan ledger + GoogleDrive + StorageConnection seçimi **51/51 PASS**, template isolation **4/4 PASS**; unit **3721**, architecture **123**, Fast Agent **361**, frontend **532** PASS. Release warnings-as-errors build, idempotent migration reapplication, pending-model kontrolü, gerçek HTTPS API/Chromium audit smoke ve gerçek çapraz-proses crash/recovery/replay smoke testi geçti.

Win11 VM'de mevcut derlenmiş probe ile **DPAPI dar smoke PASS**: process boundary round trip, pending credential persistence, plaintext absence, corruption refusal ve deletion. .NET 10 runtime/SQL Server erişimi doğrulandı; SDK veya Agent service kurulmadı. Tam Windows account/service ve MSSQL backup/restore kabulü değildir.

Fiziksel silme öncesi `backup_storage_cleanups` tablosuna intent kaydedilir, remote delete sonrası proses çökse bile sonraki sweep'te nesnenin absence'ı doğrulanıp terminal audit atomik olarak tamamlanır; duplicate sweep replay'leri tek audit event üretir. `docs/` hâlâ ignored'dır; yalnız bu iki plan güncellendi, hassas dosyalar stage edilmedi.

---

# 11. MVP'de Bilerek Yapılmayacaklar

> **Tarihsel kapsam listesi:** Aşağıdaki maddeler ilk MVP tanımı yapılırken zorunlu tutulmayan işlerdir. 2026-10-13 itibarıyla bunlardan Linux Agent host, gerçek MSSQL Full Restore, SFTP, Google Drive ve multi-storage gibi özellikler **sonradan uygulanmıştır**. Güncel eksikler için Bölüm 14 ve ileri roadmap esas alınmalıdır.

Aşağıdakiler için mimari hazırlık yapılabilir ancak MVP'nin ilk tamamlanma kriteri değildir:

- Linux Agent implementation,
- PostgreSQL backup,
- Differential MSSQL backup,
- Transaction Log backup,
- Point-in-Time Restore,
- gerçek MSSQL restore,
- VSS,
- LVM/Btrfs/ZFS snapshot,
- SFTP,
- S3,
- Azure Blob,
- Google Drive,
- FTP,
- SMB/NAS,
- multi-storage replication,
- immutable backup,
- Object Lock,
- email notification,
- Slack/Teams/Telegram,
- agent auto-update,
- backup anomaly detection,
- health score,
- restore testing,
- bandwidth limiting,
- resumable multipart upload.

Bu özelliklerin ileride eklenmesi mevcut core domain'in kırılmasını gerektirmemelidir.

---

# 12. MVP Tamamlanma Kriterleri

MVP aşağıdaki senaryo uçtan uca çalıştığında tamamlanmış kabul edilebilir. **Bu liste tarihsel kabul sözleşmesidir; testlerin bir bölümünün bulunması, tüm adımların aynı gerçek Windows ortamında tek bir uçtan uca testte onaylandığı anlamına gelmez.** Bölüm 14 güncel kod/test durumunu ayırır:

1. PlatformAdmin tenant oluşturabilir.
2. Tenant'a kullanıcı atanabilir.
3. Kullanıcı tenant context'inde çalışabilir.
4. TenantAdmin enrollment token oluşturabilir.
5. Windows Agent enroll olabilir.
6. Agent heartbeat gönderebilir.
7. Agent capabilities raporlanabilir.
8. Agent üzerindeki MSSQL instance keşfedilebilir.
9. MSSQL version/edition/database bilgileri alınabilir.
10. Database state ve recovery model okunabilir.
11. Kullanıcı MSSQL Full Backup job oluşturabilir.
12. Kullanıcı manuel backup başlatabilir.
13. Backend BackupRun oluşturabilir.
14. Agent BackupRun'ı güvenli şekilde claim/execute edebilir.
15. MSSQL Full Backup alınabilir.
16. `RESTORE VERIFYONLY` başarılı çalışabilir.
17. SourceChecksum hesaplanabilir.
18. Artifact hazırlanabilir.
19. ArtifactChecksum hesaplanabilir.
20. Backup local veya central storage'a aktarılabilir.
21. BackupReplica kaydı oluşturulabilir.
22. Remote/storage verification tamamlanabilir.
23. Backup history görüntülenebilir.
24. Run/Attempt ayrımı görüntülenebilir.
25. Scheduled backup çalışabilir.
26. Duplicate execution engellenebilir.
27. Offline grace-period davranışı çalışabilir.
28. Retry çalışabilir.
29. Cancellation çalışabilir.
30. Retention çalışabilir.
31. Soft-delete → physical-delete akışı çalışabilir.
32. Tenant'lar birbirlerinin hiçbir verisine erişemez.
33. Audit log kritik işlemleri kaydeder.
34. Backend/agent protocol incompatibility algılanabilir.
35. Sistem restart sonrasında schedule ve run state'ini kaybetmez.

---

# 13. Uygulama Sırası Özeti

```text
Foundation
    ↓
Tenancy / Identity / Security
    ↓
Agent Enrollment + Connectivity
    ↓
MSSQL Discovery
    ↓
Manual Full Backup
    ↓
Verification + Checksums
    ↓
Storage
    ↓
History
    ↓
Scheduling
    ↓
Retry / Cancellation / Concurrency
    ↓
Retention
    ↓
Web UX Polish
```

Ana prensip:

> Önce küçük ama eksiksiz çalışan dikey akış oluşturulacak. Yeni özellikler bu çalışan akışın üzerine modül olarak eklenecek; ileri özellikler uğruna temel backup yolunun tamamlanması geciktirilmeyecektir.

---

# 14. Post-MVP Durumu (2026-10-13)

Bu belge ilk MVP'nin tarihsel planıdır; yukarıdaki teknik kararlar ve 35 maddelik kabul listesi geriye dönük değiştirilmemiştir. Güncel yön, tamamlanan fazların ayrıntısı ve bir sonraki dilim için **`Gelecek_Gelistirmeler_Roadmap.md`** esas alınır.

**Kod referansı:** [`main` / `c6f66b5c764eef8081a3dd936d1778eda80d1a09`](https://github.com/mtsarac/backup-platform/tree/c6f66b5c764eef8081a3dd936d1778eda80d1a09). Main Fast CI başarılıdır; tam PostgreSQL/Windows/MSSQL/Google Drive canlı kabulünü temsil etmez.

## 14.1 Tamamlanan çekirdek ve post-MVP genişlemeleri

| Kapsam | 2026-10-13 kod durumu | Sınır / not |
|---|---|---|
| Multi-tenant auth/RBAC, PlatformAdmin, agent enrollment | Completed | Windows Service + Linux host/enrollment; Linux systemd installer yok. |
| MSSQL discovery, manual/scheduled Full Backup | Completed | Verify/checksum/staging/replica, retry, cancellation, concurrency, retention. |
| MSSQL Full Restore | Completed | API, Agent ve Restore Wizard; differential/log/PITR restore kapsam dışı. |
| Storage | Completed (temel) | Local/Central, SFTP, Google Drive, canonical multi-destination. Gerçek Google Drive canlı API acceptance bekliyor. |
| Managed Service / Quota | Completed (temel) | Plan, subscription, tenant usage, reservation, admission ve quota state. |
| Backup Browser, Download, Accessible UI | Completed | Owner/Admin kontrollü, SHA-256 doğrulamalı download; dashboard, wizard ve erişilebilirlik iyileştirmeleri. |
| Support Access ve support restore | Completed (tanımlı kapsam) | Gerekçe/permission/TTL/audit ve müşterinin SQL hedefine sınırlandırılmış restore; izole support restore sandbox ayrı Future. |
| Repair/Retention hardening | Completed (kod ve test) | Lease fencing, durable orphan/quarantine, atomic quota/concurrency, cancelled/stale recovery. |
| Kritik operasyon audit'i | **Completed — A0 / A0.1, çalışma dalı (2026-10-14)** | Bölüm 10 ve roadmap Bölüm 1B olay/servis/transaction/actor/tenant/test matrisi; durable orphan ledger (`backup_storage_cleanups`), explicit scope ve crash-window/gerçek-provider kabul sınırları korunur. |

| Frontend UI/UX Redesign & Storage Hub | **Completed — çalışma dalı (2026-10-15)** | Yeni tasarım sistemi, responsive masaüstü/mobil, Storage çok adımlı sihirbaz ve detay sekmeleri, 64 test dosyası / 622 test PASS, gerçek Chromium doğrulaması. |
| PostgreSQL Test Suite Optimizasyonu & Profiling | **Completed — çalışma dalı (2026-10-15)** | 1.695 entegrasyon testinin detaylı süre profili çıkarıldı (en yavaş 30 test/sınıf); SeedBatchAsync ile çoklu artifact seeding'i optimize edildi, UploadPacerTests kararsızlığı deterministik senkronizasyonla giderildi, transfer lease bekleme süreleri kısaltıldı, 1.695/1.695 test 0 hata ile doğrulandı. |
| MSSQL Windows Full Backup & Overwrite E2E | **Completed — çalışma dalı (2026-10-15)** | Windows 11 VM SQL Server 2025 üzerinde NewDatabase ve ReplaceExistingDatabase (confirmOverwrite korumalı) canlı kabul testleri PASS. Precheck OverwritePolicy şema hatası düzeltildi. |
| Platform Metadata DR Tatbikatı | **Completed — çalışma dalı (2026-10-15)** | `backup_platform_dev` veritabanının binary formatta tam dump ve restore tatbikatı icra edildi (`docs/Platform_Metadata_Disaster_Recovery.md`). |
**Birleştirilen işler:** `fix/backup-safety-hardening` (PR #12) ve `feat/accessible-ui` (PR #5) artık `main` içindedir. Daha sonra bağımlılık/README güncellemeleri de entegre edilmiştir.

## 14.2 35 MVP kriteri için kabul yaklaşımı

- Bölüm 12'nin 1–32 ve 34–35 kriterlerinin büyük bölümüne karşılık gelen ürün kodu ve otomatik testler bulunmaktadır; her birinin **güncel branch üzerinde gerçek Windows Agent + MSSQL ile uçtan uca yürütüldüğü** ayrıca belgelenmelidir.
- **Madde 33 (kritik audit olayları): A0 çalışma dalında kapatıldı.** Bölüm 10 ve roadmap Bölüm 1B minimum olay matrisi, atomik mutation/audit, gerçek actor, tenant isolation, replay ve sır saklama kanıtını ayrı ayrı kaydeder; full-suite veya gerçek-provider kabulü olarak yorumlanmaz.
- Geçmiş raporlarda PostgreSQL integration ve SFTP container smoke başarıları vardı. **A1 (2026-10-14, çalışma dalı):** gerçek Google Drive (taze OAuth, refresh, 577 KB ve 122 MB upload/ReadBack/download/restore kaynağı/kesintili yükleme + otomatik retry/silme/revoke ve Google tarafı iptal tespiti) ve gerçek Windows 11 VM (service kurulum/enrollment, DPAPI, heartbeat/capability sync, MSSQL discovery, Full Backup, NewDatabase restore, iptal, service restart ve ağ kesintisi recovery; ayrıca aynı dev tenant'a bağlı Windows Agent'tan Drive'a backup/download/restore/restart/iptal/silme) senaryoları **PASS**; senaryo matrisi roadmap Bölüm 1B'dedir. A1 üç gerçek hata buldu ve düzeltti (backend-relayed upload ~28,6 MB sınırı; yeni NuGet advisory'leri nedeniyle kırılan `-warnaserror` restore; SFTP'yi adlandıran provider-bağımsız bağlantı testi mesajları). **NOT RUN:** Windows Agent'tan SFTP hedefi, multi-destination canlı koşusu, Windows domain/özel service account ve overwrite-restore, Drive'da gerçek ağ kesintisi, Drive kota aşımı, tam PostgreSQL suite'i (NOT VERIFIED). 35 kriterin her biri tek bir Windows E2E koşusunda ayrı ayrı imzalanmış değildir; kodun varlığı bu riskleri tek başına kapatmaz.
- Bu belge bir **deployment readiness/onay belgesi değildir**; halen geliştirme dönemi devam etmektedir.

## 14.3 Henüz uygulanmayan ileri özellikler

Bandwidth profilleri (2B) ve upload pause/resume (2C) — byte-temelli upload limiti (2A) tamamlandı; File/Folder Backup ve File Restore; Windows VSS ve Linux snapshot; Docker runtime; Linux systemd installer; MSSQL Differential/Transaction Log/PITR; PostgreSQL backup; S3/SMB/FTPS; tam 3-2-1 storage-class/off-site policy; otomatik restore testleri (R1); Teams/Slack/Telegram/push bildirimleri (email/webhook/SMS bildirimleri N1 ile eklendi; SMS `IMPLEMENTED — NOT VERIFIED`); kapasite/health/anomaly; auto-update; artifact encryption (E1); ileri audit/raporlama. **Online billing/provider bu eksikler listesine dahil değildir: bilinçli biçimde kapsam dışıdır.**

FileSystem backup ve snapshot modüllerinin `.csproj` iskeletlerinin bulunması bu özelliklerin tamamlandığı anlamına gelmez. SFTP ve Google Drive ise önceki 2026-10-07 listelerinin tersine temel işlevleriyle kodda vardır.

## 14.4 Değerlendirme raporu ile MVP gereksinimi uzlaştırması

- **Kodda zaten mevcut olduğu doğrulananlar:** `BackupConfiguration.RequiredVerifiedReplicaCount`, `BackupDestination.IsPrimary`, `BackupReplicationEvaluator`, `StorageConnection.Ownership` / `QuotaScope` ve ilgili kota selector testleri. Ek rapordaki “required replica sayısı ve ownership eksik” iddiaları kodun mevcut durumunu yansıtmıyor; aynı özellik yeniden implemente edilmez. Tam storage sınıfı/off-site 3-2-1 policy ise ayrıdır.
- **Kapatılan MVP borcu:** Kritik audit (Bölüm 10 / madde 33) için login/logout, job değişiklikleri, manual/scheduled run, retry/cancellation/terminal sonuç, retention/verified physical delete, agent enrollment/revoke/rotate ve mevcut storage/secret/support/download/restore kapsamı A0 matrisiyle doğrulandı. Olmayan hard-delete/agent-disable yönetim API'leri yeni özellik olarak eklenmedi.
- **Örnek risk sınırı:** Code review ve Fast CI başarısı, gerçek Google Drive OAuth/API ve Windows DPAPI/MSSQL acceptance yerine geçmez. Büyük repair lease recovery backlog'unda `Take(50)` görülmesi tek başına bug değil; ilerleme testinde starvation gösterilirse düzeltilir.
- **Teknik/operasyonel backlog:** Müşteri/tenant yedek verilerinin silinmesi ve veri saklama/KVKK süreçleri; platformun metadata PostgreSQL veritabanı için backup ve restore tatbikatı; backend–agent uyumluluk/sürüm politikası; logs/metrics/tracing. Bunlar yeni engine fazından bağımsız değerlendirilir; deployment talimatı değildir.
- **Belge güvenliği:** Git `.gitignore` içinde `docs/` var. Belgeleri sürüm kontrolüne almak istenirse yalnızca seçili, gözden geçirilmiş `.md` dosyaları eklenir; credential/secret içeren yerel dosyalar asla stage edilmez.

## 14.5 Satış ve abonelik yaklaşımı

**Karar:** Uygulama müşteriye yüz yüze/doğrudan satılır; teklif, sözleşme, fatura ve ödeme uygulama dışında yönetilir. Mevcut `Plan`, `TenantSubscription`, kota/policy ve PlatformAdmin manuel atama akışları korunur. Online ödeme sayfası, kart saklama, ödeme webhook'u, faturalama modülü veya billing provider **geliştirilmez**. Satış modeli değişirse ayrı karar verilir; şimdiden billing abstraction/migration oluşturulmaz. Ödeme gecikmesi otomatik backup silme kararı doğurmaz.

## 14.6 Sıradaki geliştirme ve kabul ilkesi

**A0 kritik audit, A1 canlı provider/Windows acceptance kapanışı, Faz 2A (gerçek upload bandwidth limiting) ve N1 bildirimleri (email, webhook, SMS) çalışma dalında tamamlandı**; Faz 2B/2C başlamadı. **N1'de Email ve Webhook doğrulandı; SMS `IMPLEMENTED — NOT VERIFIED`'dır** (gerçek sağlayıcıya karşı çalıştırılmadı, SMS'e özel test yazılmadı; kayıt roadmap Bölüm 26). Kullanıcı isteğiyle test kurulum maliyeti ve odaklı geliştirme runner'ları iyileştirildi; integrity/tenant/race test kapsamı azaltılmadı. Ardından roadmap Bölüm 35'te **R1** (izole MSSQL restore test otomasyonu) ve **E1** (artifact şifreleme) öne alınır.

Her teslimatta: ilgili servis/senaryo için unit+DB/provider/Windows test kapsamı, tenant/secret güvenliği, cancel/retry/restart/failure akışları, geriye uyumluluk ve `NOT RUN` kabul listesi raporlanmalıdır. Kod varlığı tek başına “gerçek ortam kabulü geçti” anlamına gelmez. Deployment ve release kararı verilmez.

Ayrıntılı kabul, tasarım ve öncelikler için tek aktif kaynak: `Gelecek_Gelistirmeler_Roadmap.md` → **Bölüm 1B (A0), Bölüm 4 (Faz 2), Bölüm 26 (N1 bildirimleri), Bölüm 35 (iş sırası)**.
