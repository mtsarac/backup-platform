# R1 — İzole Restore Doğrulaması: Tasarım, Yetki/Cleanup Modeli ve Kabul Matrisi

**Tarih:** 2026-10-15  
**Referans:** `docs/Gelecek_Gelistirmeler_Roadmap_2026-10-13_Revize.md` (Bölüm 22 / Faz 20 / R1)  
**Kapsam:** MSSQL ve PostgreSQL **Full** backup artifact'ları; yalnız **manuel** tetikleme. Periyodik/otomatik zamanlayıcı sonraki dilimdir.  
**Durum:** Uygulandı ve kabul edildi (kanıt Bölüm 9; çalıştırılmayanlar açıkça işaretli).

---

## 1. İlke

> "Backup'ın var olması, yedeğin geri yüklenebileceği anlamına gelmez."

Kullanıcının seçtiği artifact, **hiçbir mevcut veritabanına dokunmadan**, hedef SQL sunucusunda geçici ve platformun adlandırdığı bir veritabanına geri yüklenir; açılış, bütünlük ve veri okunabilirliği kanıtlanır; veritabanı düşürülür. Sonuç `Restored`, `Failed` veya `NotRun` olarak, süre, hata nedeni ve audit kaydıyla saklanır.

## 2. Akış

```text
POST /backup-artifacts/{id}/restore-verifications        (TenantOperator, Idempotency-Key)
  → backend disposable adı üretir: bp_verify_<32 hex>    (kullanıcı ad/mod/overwrite veremez)
  → RestoreOperation(Purpose=Verification, Mode=NewDatabase), replica pin, lease/fencing   [mevcut restore altyapısı]
  → Agent: pre-check → indirme → SHA-256 → motor doğrulaması → plan
         → restore (IsolatedVerification) → Verifying → CleaningUp (DROP) → Completed/Failed
  → sonuç + cleanup durumu aynı completion'da; audit: restore.verification.requested / .completed
```

Mevcut restore boru hattı yeniden kullanılır (claim, lease, fencing, retention pin, quota/staging kontrolü, çoklu replica erişimi). Yeni olan: `RestorePurpose.Verification`, motorun `IRestoreVerificationProvider` yeteneği (doğrulama + drop), `Verifying`/`CleaningUp` aşamaları ve backend'de izlenen cleanup kaydı.

## 3. İzolasyon ve güvenlik sınırı

| Kural | Uygulama |
|---|---|
| Üretim DB'sine dokunulmaz | Yalnız yeni `bp_verify_*` DB oluşturulur; `ReplaceExistingDatabase` ve kullanıcı adı bu amaçta **yasaktır** (domain + DB `CHECK` + API). Doğrulamanın `TargetState` değeri **her zaman `NotModified`**. |
| Rezerve adlar | `bp_verify_*` ve `bp_old_*` kullanıcı restore'unda hedef olamaz (domain, store, provider). Platform yalnız `^bp_verify_[0-9a-f]{32}$` / `^bp_old_[0-9a-f]{32}$` adlarını düşürür; agent ve provider adı **yeniden** doğrular. |
| Tenant izolasyonu | Tüm okuma/yazma tenant-qualified; başka tenant'ın restore/artifact'ı `404`. Agent cleanup'ı yalnız kendi tenant+agent'ına verilir; token'sız/yanlış token `409`, başka agent `404`. |
| Yetki | Başlat/iptal/cleanup retry: **TenantOperator**. Rollback kopyası kaldırma: **TenantAdministrator** + tam veritabanı adı onayı + guarded retention. Viewer yalnız okur. Support oturumu verification başlatamaz ve cleanup görmez. |
| Sır | Kimlik bilgisi yalnız agent-yerel referanstan çözülür; sonuçlarda motor/istisna metni yok, yalnız sabit cümle + kararlı kod. |
| Lease/fencing | Doğrulama bir restore attempt'idir (aynı lease/fencing). Cleanup ayrı bir lease + token ile verilir; eski lease'in raporu `409`. |
| Kota/retention | Artifact okunur, hiçbir artifact/kota tüketilmez; replica pin'i işlem boyunca tutulur ve her terminal geçişte aynı işlemde bırakılır. |

## 4. Doğrulama kontrolleri

| Motor | `database-online` | `integrity` | `data-readable` |
|---|---|---|---|
| SQL Server | `sys.databases.state_desc = ONLINE` | `DBCC CHECKDB` (tam, `PHYSICAL_ONLY` değil) | her kullanıcı tablosunda `COUNT_BIG(*)` (en çok 500 tablo) |
| PostgreSQL | bağlanılabilir, `datallowconn` | katalog sağlığı (geçersiz/hazır olmayan index yok); restore index/kısıtları sıfırdan kurduğu için ek bir `amcheck` **gerekmez/kullanılmaz** | her kullanıcı tablosu `t::text` ile tam okunur (TOAST dahil) + large-object sayısı; `statement_timeout` |

Arşiv/yedek dosyasının kendisi de `RESTORE VERIFYONLY` / `pg_restore` tam-okuma ile ve SHA-256 ile doğrulanır (mevcut davranış).

**Sonuç eşlemesi (tek doğruluk kaynağı: backend):** `Succeeded → Restored`, `Failed/AgentLost → Failed`, `Cancelled → NotRun` (hiçbir şey kanıtlanmadı), aktifken `null`; hiç test yoksa UI `NotRun` ("Henüz test edilmedi").

## 5. Cleanup ve kurtarma

Disposable adı backend tarafından **kaynak oluşmadan önce** üretilir; bu yüzden agent çökse bile bilinmeyen bir artık kalmaz. Cleanup durumu operasyon satırında kalıcıdır:

| Durum | Anlam |
|---|---|
| `Idle` | Verification sürüyor / cleanup gerekmiyor |
| `Pending` | Kaldırma borçlu (agent kayıp, iptal/timeout sonrası drop başarısız, drop raporlanmadı) |
| `Completed` | Veritabanının olmadığı agent tarafından doğrulandı (Dropped veya NotFound) |
| `Retained` | Yalnız PostgreSQL replace restore'unun `bp_old_*` rollback kopyası; **kendiliğinden asla silinmez** |

* Agent her verification'ı kendi bitişinde (başarı, hata, iptal, timeout) **iptal edilmiş token'dan bağımsız, sınırlı süreli** bir drop ile temizler ve sonucu (`Dropped/NotCreated/Failed`) completion'a koyar.
* Agent çökerse/lease biterse operasyon `AgentLost` olur ve cleanup `Pending` kalır; backend bunu agent'ın bir sonraki poll'unda **ayrı lease'li bir görev** olarak verir (restore/backup canlıyken verilmez). Başarısızlıkta üstel backoff (1 dk … en çok 1 sa), 8 otomatik denemeden sonra "dikkat gerekiyor" durumuna park edilir; operatör **"Temizliği yeniden dene"** ile bütçeyi sıfırlar. Bütün adımlar audit'lenir (`restore.cleanup.requested|completed|failed`).
* Backend restart güvenlidir: bütün durum DB'dedir; agent çağrıları geçici hatada yeniden denenir, tamamlanma tekrarı aynı sonuçla kabul edilir.
* Drop idempotenttir (olmayan DB = başarı); drop yalnız adı kalıba uyan DB'yi hedefler.

### `bp_old_*` rollback kopyası politikası

* Replace restore başarılıysa provider `bp_old_<hex>` adını sonuçta bildirir; backend operasyona `Retained` olarak yazar. **Hiçbir arka plan görevi silmez.**
* Operatör kaldırma isteği: yalnız **TenantAdministrator**, veritabanı adını aynen yazarak, `Restores:RollbackRetention` (varsayılan **7 gün**) dolduktan sonra (`409 retention_not_elapsed` öncesi). Backend yalnız agent görevini planlar; agent drop'u yapar ve PostgreSQL'de DB yorumundaki işaret (`provider=backup-platform`, `phase=previous`) yoksa **reddeder** (`rollback-marker-missing`).
* Bu özellikten önce oluşmuş veya platform kaydında olmayan `bp_old_*` / `bp_restore_*` veritabanları **izlenmez ve asla silinmez**; operatör elle inceleyip karar verir.

## 6. PostgreSQL'de superuser ihtiyacı ve minimum yetki

Normal (üretim) PostgreSQL restore'u **superuser** gerektirmeye devam eder; nedeni sahipliği ve ACL'i korumaktır (`ALTER … OWNER`, `GRANT/REVOKE`, `ALTER DATABASE … OWNER/SET`, untrusted extension'lar, event trigger'lar). Bu, bilinçli ve belgelenmiş bir sınırdır.

**Verification superuser gerektirmez.** Gerçek PostgreSQL 18 üzerinde yalnız `CREATEDB` yetkili (superuser/createrole/replication değil) bir login ile `pg_restore --no-owner --no-acl --no-tablespaces` + doğrulama + `DROP DATABASE … WITH (FORCE)` çalışır:

* `CREATE DATABASE … TEMPLATE template0 ENCODING/LOCALE` yalnız `CREATEDB` ister; oluşturan sahip olur.
* Tablo/index/sequence/large object/trusted extension sahibi login olduğu için sorunsuz geri yüklenir; yabancı owner/ACL'ler `--no-owner/--no-acl` ile sorunsuz atlanır.
* Untrusted extension (ör. `dblink`) gerektiren arşiv **hızlıca ve sabit bir kodla** (`extension-requires-superuser`) başarısız olur; geride düşürülebilir bir DB kalır.
* Aynı login doğrulamayı ve drop'u yapar (nesneler ona aittir). Başka bir rolün açık oturumu varsa `DROP … WITH (FORCE)` `permission-denied` verir ve DB yerinde kalır (cleanup `Pending`).
* Güvenlik kazancı: arşivin SQL'i superuser yetkisiyle değil, kısıtlı login ile çalışır.

Kabul ortamında verification kimliği ayrı bir `bp_r1_verifier` rolüdür; yedekleme/replace restore'u süper kullanıcı referansını kullanır.

## 7. API

| Yöntem | Yol | Rol |
|---|---|---|
| POST | `/api/tenants/{t}/backup-artifacts/{a}/restore-verifications` | TenantOperator |
| GET | `/api/tenants/{t}/restores?purpose=Verification&artifactId=…` | Viewer |
| GET | `/api/tenants/{t}/restores/{id}` | Viewer (`verificationOutcome`, `cleanup`, `result.verification`) |
| POST | `/api/tenants/{t}/restores/{id}/cancel` | Operator |
| POST | `/api/tenants/{t}/restores/{id}/cleanup/retry` | Operator |
| POST | `/api/tenants/{t}/restores/{id}/rollback-removal` `{confirmDatabaseName}` | TenantAdministrator |
| POST | `/api/agent/restore-cleanups/{operationId}/complete` | Agent (lease token) |

Agent yetenekleri: `mssql.restore.verification`, `postgres.restore.verification`. Bu yeteneği bildirmeyen (eski) agent verification için seçilemez (`422 agent_cannot_restore`). Migration `RestoreVerificationR1` mevcut satırlara dokunmaz (`CleanupKind='None'`, `CleanupState='Idle'`).

## 8. Bilinen sınırlar

* Periyodik/otomatik restore testi yoktur (sonraki dilim); yalnız manuel tetikleme.
* PostgreSQL için hedef sunucuda disk alanı SQL ile doğrulanamaz; kapasite yalnız agent staging alanında ve restore sırasında ("disk dolu") yakalanır. MSSQL hedef disk planlayıcısı mevcuttur.
* `amcheck` kullanılmaz; PostgreSQL bütünlüğü "tam restore + katalog sağlığı + tam okuma"dır.
* Windows Agent + PostgreSQL istemci araçları bu iterasyonda doğrulanmadı.
* Cleanup yalnız agent erişilebilir olduğunda ilerler; agent kalıcı olarak yoksa kayıt `Pending` kalır ve UI'da görünür (silinmez, sayılmaz).

## 9. Kabul matrisi

Tüm senaryolar gerçek Docker SQL Server, PostgreSQL 18 ve Windows 11 VM SQL Server instance'ları üzerinde `scripts/frontend/run-r1-smoke.sh` ve `scripts/frontend/r1-acceptance.py` ile çalıştırılmıştır.

| No | Senaryo | Motor / Ortam | Sonuç | Doğrulanan Kriterler |
|---|---|---|---|---|
| 1 | Sağlam artifact -> izole restore -> PASS | MSSQL (Docker) | **PASS** | `bp_verify_*` veritabanı açıldı, DBCC CHECKDB geçti, tablo satırları okundu, DB DROP edildi, geride artık kalmadı (0.0s). |
| 2 | Sağlam artifact -> izole restore -> PASS | PostgreSQL (Docker) | **PASS** | `CREATEDB` yetkili `bp_r1_verifier` rolü ile restore, katalog sağlığı ve TOAST detoast tam okuma geçti, `DROP DATABASE WITH (FORCE)` ile temizlendi. |
| 3 | Bozuk artifact (bit-flip) | MSSQL | **PASS** | SHA-256 sağlama uyumsuzluğu (`ChecksumMismatch`) yakalandı, DB açılmadı, staging temizlendi. |
| 4 | Kesik artifact (truncated) | MSSQL | **PASS** | `ChecksumMismatch` ile reddedildi. |
| 5 | Eksik artifact (silinmiş replica) | MSSQL | **PASS** | `ReplicaUnavailable` ile güvenle durduruldu. |
| 6 | Bozuk artifact (bit-flip) | PostgreSQL | **PASS** | SHA-256 uyumsuzluğu yakalandı, target server ellenmedi. |
| 7 | Kesik artifact (truncated) | PostgreSQL | **PASS** | `ChecksumMismatch` ile reddedildi. |
| 8 | Eksik artifact | PostgreSQL | **PASS** | `ReplicaUnavailable`. |
| 9 | SQL Credential hatası (yanlış şifre) | MSSQL | **PASS** | `AuthenticationFailed`, kullanıcı dostu sabit hata mesajı, DB dokunulmadı. |
| 10 | SQL Credential hatası (yanlış şifre) | PostgreSQL | **PASS** | `AuthenticationFailed`. |
| 11 | Bilinmeyen credential referansı | MSSQL | **PASS** | `InvalidConfiguration` olarak reddedildi, `NotCreated` cleanup. |
| 12 | SQL sunucu erişilemez (kapalı port) | MSSQL | **PASS** | `DatabaseUnavailable`. |
| 13 | SQL sunucu erişilemez (kapalı port) | PostgreSQL | **PASS** | `DatabaseUnavailable`. |
| 14 | Yetersiz kapasite (kısıtlı staging tmpfs) | PostgreSQL | **PASS** | 6 MB tmpfs staging alanında 25+ MB yedek için `InsufficientDiskSpace` ile pre-check/indirme aşamasında durduruldu. |
| 15 | Cancel ve Retry | MSSQL | **PASS** | `Restoring`/`Verifying` aşamasında iptal edildi, geçici DB temizlendi, `NotRun` işaretlendi; hemen ardından retry PASS (`Restored`). |
| 16 | Cancel ve Retry | PostgreSQL | **PASS** | İptal sonrası `bp_verify_*` silindi (`NotRun`); retry PASS (`Restored`). |
| 17 | Agent çökmesi (SIGKILL) ve kurtarma | MSSQL | **PASS** | Restore ortasında agent öldürüldü -> `AgentLost` -> cleanup `Pending` -> agent restart sonrası temizlik otomatik kiralandı ve tamamlandı. |
| 18 | Agent çökmesi (SIGKILL) ve kurtarma | PostgreSQL | **PASS** | `AgentLost` sonrası pending cleanup agent tarafından otomatik tamamlandı. |
| 19 | Agent çökmesi + Cleanup başarısızlığı | MSSQL | **PASS** | DB durduruldu -> agent temizleme denemesi `connection-failed` ile başarısız oldu (Attempt 1) -> DB açıldı -> operatör retry ile temizlik tamamlandı. |
| 20 | Agent çökmesi + Cleanup başarısızlığı | PostgreSQL | **PASS** | Disposable DB üzerinde açık oturum tutuldu -> `permission-denied` ile drop başarısız oldu -> oturum kesildi -> retry ile drop başarılı. |
| 21 | Backend restart sırasında doğrulama | MSSQL | **PASS** | API process öldürülüp yeniden başlatıldı; agent yeniden bağlanıp sonucu iletti, `Restored` ve cleanup `Completed`. |
| 22 | Backend restart sırasında doğrulama | PostgreSQL | **PASS** | API restart sonrası doğrulama başarıyla tamamlandı. |
| 23 | Rollback kopyası (`bp_old_*`) saklama & silme | PostgreSQL | **PASS** | Replace restore eski DB'yi sakladı (`Retained`); erken silme `409 retention_not_elapsed`; isim uyuşmazlığı `400`; süre sonunda admin onayıyla silindi; sahte `bp_old_*` dokunulmadı. |
| 24 | Tenant izolasyonu ve açık roller | Çapraz tenant | **PASS** | Yabancı tenant REST doğrulamalarını göremedi/yönetemedi (`404`/`403`). Viewer başlatamadı (`403`); Operator ve Admin başlattı (`202`); Operator rollback silemedi (`403`). |
| 25 | Denetim izi (Audit log) | Platform | **PASS** | `restore.verification.requested` ve `restore.verification.completed` süre ve detaylarla audit event olarak yazıldı. |
| 26 | Windows VM SQL Server 2025 Doğrulaması | Windows 11 VM | **PASS** | VM üzerindeki SQL Server 2025'te izole restore, `DBCC CHECKDB`, tablo okuma, drop, bozuk dosya reddi ve drop idempotency doğrudan doğrulandı. |
| 27 | Gerçek Chromium UI Smoke | Web / React | **PASS** | Chromium üzerinden login, Restores -> "Restore tests" görünümü, filtreler ve arayüz akışı başarıyla doğrulandı. |
