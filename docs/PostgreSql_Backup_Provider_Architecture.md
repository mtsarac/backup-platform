# PostgreSQL Full Backup/Restore — Mimari ve Gerçek Uygulama (Faz 13)

**Tarih:** 2026-10-15 · **Modül:** `BackupPlatform.BackupProviders.PostgreSql` · **Kapsam:** yalnız Full logical Backup ve Full Restore
**Referans:** `docs/Gelecek_Gelistirmeler_Roadmap_2026-10-13_Revize.md` Bölüm 15 / Faz 13

## 1. Kapsam

Kapsam içi: database discovery, capability tespiti, pre-check, `pg_dump --format=custom` Full Backup, tam okuma doğrulaması, SHA-256 (pipeline), NewDatabase ve onaylı ReplaceExistingDatabase restore.
Kapsam dışı (bilinçli): `pg_basebackup`, WAL arşivleme, PITR, Differential/Incremental, `pg_dumpall`/cluster rolleri ve File/Folder. İzole restore doğrulaması (R1) ayrı bir dilimdir: `docs/R1_Restore_Verification_Design.md`.

MSSQL kodu PostgreSQL'i, PostgreSQL kodu MSSQL'i bilmez. Ortak olan: `IDatabaseBackupProvider`/`IDatabaseRestoreProvider`, `DatabaseBackupPipeline`/`DatabaseRestorePipeline`, çoklu hedef, bandwidth, retry, lease/fencing, retention, quota, audit, download.

## 2. Çalışma modeli

Agent, PostgreSQL sunucusuna TCP ile bağlanır; sunucunun dosya sistemine yazmaz. `pg_dump`/`pg_restore` **Agent tarafında** çalışır, staging dizininin yalnız Agent tarafı kullanılır (`Mapped` staging'de sunucu dizini PostgreSQL için anlamsızdır). Artifact adı platform grammar'ına uymak için `*_FULL.bak` kalır; içerik `PGDMP` custom arşividir ve indirme dosya adı `.dump` olur.

| Konu | Karar |
|---|---|
| Tutarlılık | `pg_dump` tek snapshot'tan dump alır; çıktı `stdout` ile yalnız `CreateNew` açılan staging dosyasına akıtılır (mevcut dosya ezilmez). |
| Doğrulama | `pg_restore --exit-on-error --file=-` tüm sıkıştırılmış veri ve large-object bloklarını okur (yalnız `--list` değil). Method: `pg_restore-full-read`. |
| Checksum | Pipeline SHA-256'sı her zaman. `data_checksums` politikası sunucunun sayfa doğrulamasıdır; `Required` yalnız `data_checksums=on` kümelerde geçer. |
| Sıkıştırma | `Off` → `--compress=0`, aksi halde `--compress=6`. |
| CopyOnly | Mantıksal dump yedek zincirini etkilemez; UI seçeneği göstermez, `Applied.CopyOnly` istenen değeri izler. |
| Kimlik bilgisi | Parola yalnız Agent-yerel referanstan çözülür; process ortamında `PGPASSWORD` olarak verilir, komut satırında/DB'de/API'de yoktur. Agent ortamındaki tüm `PG*` değişkenleri tool'a geçmeden temizlenir. |
| Injection | Argümanlar `ArgumentList` ile verilir (shell yok). Veritabanı adı tırnaklı libpq conninfo'sunun içine konur (`dbname='…'`), böylece `=`/URI içeren ad parametre olarak yorumlanamaz. Ad ≤ 63 UTF-8 byte (sunucu sessizce kırpar). |
| Süreç güvenliği | Timeout ve iptalde process ağacı öldürülür; stdout/stderr eşzamanlı boşaltılır; provider dönmeden child ve pump'lar join edilir. |
| Hata sınıfları | SQLSTATE (bağlantı) + stderr deseni (tool); kullanıcıya yalnız sabit mesaj; ham tool metni agent loguna gider. |

### Pre-check
Tool var/çalışıyor, tool sürümü ≥ sunucu sürümü, kimlik doğrulama, DB var/bağlanılabilir, tüm tabloların/sequence'lerin okunabilirliği, RLS bypass, large-object yetkisi, staging erişimi/alan. **Reddedilenler:** foreign table, replication subscription ve standby (sessizce eksik veri içeren "Full" artifact yayınlanmaz).

### Restore
Provider, `pg_restore --list --create` ve `--use-list` ile yalnız DATABASE/EXTENSION girdilerinden metadata çıkarır; fonksiyon gövdesi gibi serbest SQL asla yorumlanıp uygulanmaz.
1. Arşiv `PGDMP` magic, TOC, tam okuma doğrulaması, source/dump/restore/target sürüm uyumu, extension varlığı.
2. Benzersiz **scratch** veritabanına (`bp_restore_*`) `pg_restore --exit-on-error` ile restore; hedef adı değişmez.
3. **NewDatabase:** hedef var ise reddedilir; başarılıysa scratch yeni ada atomik olarak yeniden adlandırılır. Hata/iptalde yalnız bu denemenin OID'siyle doğrulanan scratch silinir (`NotModified`).
4. **ReplaceExistingDatabase** (API'de `confirmOverwrite` = hedef adı zorunlu): kilit altında tek transaction'da eski DB `bp_old_*` olarak, scratch hedef adı olarak yeniden adlandırılır. Eski veri **asla silinmez**; bağlantıları kapalı kurtarma kopyası olarak kalır (operatör kontrol edip temizler). Yayın öncesi her hata/iptal hedefi değiştirmez.
5. Sahiplik/ACL/large object/extension korunur; bu nedenle **normal** restore için hedef login'in **superuser** olması gerekir (aşağıdaki "Superuser ihtiyacı" bölümü). Cluster rolleri arşivde yoktur; eksik rol scratch'te güvenle başarısız olur.

Arşiv güveni: SHA-256 bayt bütünlüğünü doğrular; kaynak SQL'in zararsızlığını doğrulamaz (superuser ile çalışır). Yalnız platformun kendi artifact'ları geri yüklenmelidir.

## 3. Platform entegrasyonu
- Restore motoru artifact'ın **değişmez** `SourceMetadataJson.engine` alanından türetilir (eksikse eski MSSQL); kullanıcı host/port'u motoru belirlemez. Capability kapıları create, claim ve finalize adımlarındadır; backup claim'i `postgres.backup.full` olmayan Agent'a PostgreSQL işi vermez.
- Agent capability'leri: `postgres.backup.full|compression|checksum|copy-only`, `postgres.restore.full|precheck|discovery|verify`; differential/incremental/WAL `Unsupported`.
- Konfigürasyon: `Agent:Backup:PostgreSql:ClientToolsDirectory` (mutlak dizin; boşsa `PATH`).
- Logical plan'da fiziksel dosya yoktur: `RestorePlan` boş mapping taşıyabilir (yalnız PostgreSQL için sonuç doğrulaması boş mapping kabul eder).

## 4. Sınırlar ve bilinen riskler
- `kill -9` ile ölen Agent staging'de kısmi `.bak` bırakır; mevcut staging temizleyicisi (varsayılan 12 sa) siler. Scratch (`bp_restore_*`) ve `bp_old_*` veritabanları otomatik silinmez. R1 ile birlikte **yeni** replace restore'ların `bp_old_*` kopyası backend'de `Retained` olarak izlenir ve yalnız tenant yöneticisi isteğiyle (guarded retention + ad onayı + DB işareti) kaldırılır; önceden oluşmuş kopyalar izlenmez ve asla otomatik silinmez.
- Hedef sunucuda disk alanı SQL ile doğrulanamaz.
- Windows Agent + PostgreSQL client araçları bu iterasyonda **doğrulanmadı**.
- Normal restore superuser gerektirir (sahiplik/ACL/untrusted extension korunur). **İzole doğrulama restore'u** (`IsolatedVerification`) yalnız `CREATEDB` yetkili bir login ile çalışır; bkz. aşağıdaki bölüm.

## 5. Platform Metadata DR ile ayrım
Ürün PostgreSQL provider'ı tenant verisini yedekler; `docs/Platform_Metadata_Disaster_Recovery.md` platformun kendi metadata veritabanı içindir ve bu iş tarafından değiştirilmemiştir.

## 6. Superuser ihtiyacı ve minimum yetki (R1 incelemesi)

Gerçek PostgreSQL 18 üzerinde yalnız `CREATEDB` yetkili (superuser/createrole/replication olmayan) bir login ile ölçüldü.

**Normal restore'da superuser'ın gerçekten gerektiği yerler:** nesne sahipliğinin korunması (`ALTER … OWNER`, restore eden login ilgili rollerin üyesi değilse), ACL'ler (`GRANT/REVOKE`), veritabanı düzeyi `ALTER DATABASE … OWNER/SET` ve veritabanı ACL'leri, untrusted extension'lar (ör. `dblink`, `amcheck`), event trigger'lar ve untrusted procedural language'ler.

**Superuser gerektirmeyenler:** `CREATE DATABASE … TEMPLATE template0 ENCODING/LOCALE` (yalnız `CREATEDB`), restore eden login'in sahibi olduğu tablo/index/sequence/large object, trusted extension'lar, `COMMENT ON DATABASE` işareti ve sahibin `DROP DATABASE … WITH (FORCE)` çağrısı.

**Sonuç:** İzole doğrulama `--no-owner --no-acl --no-tablespaces` ile, hedefte doğrudan `bp_verify_*` adıyla (scratch/yeniden adlandırma/yayınlama yok) yalnız `CREATEDB` ile çalışır; superuser-only extension gerektiren arşiv `extension-requires-superuser` koduyla hızlı ve güvenli başarısız olur. Normal restore'un superuser gereksinimi, sahiplik/ACL bütünlüğünü korumak için bilinçli olarak sürer; "mümkünse en az yetki" ilkesi doğrulama yolunda uygulanmıştır. Başka bir rolün açık oturumu varsa sahibin `DROP … WITH (FORCE)` çağrısı `permission-denied` verir ve veritabanı yerinde kalır (cleanup kaydı `Pending`).

