# Platform Metadata Disaster Recovery Prosedürü ve Tatbikat Raporu

**Tarih:** 2026-10-15  
**Veritabanı Motoru:** PostgreSQL 18  
**Hedef:** Platformun metadata veritabanı (`backup_platform_dev` / üretim DB) çökmesi veya kaybedilmesi durumunda tam geri yükleme prosedürü ve tatbikat kanıtı.

---

## 1. Mimari Sınırlar ve Korunan Varlıklar

Platformun metadata veritabanı şunları içerir:
- Multi-tenant üyelikler, kullanıcılar, DPAPI korumalı kimlik bilgileri ve platform rolleri
- BackupJob tanımları, cron takvimleri, overlap ve retry politikaları
- BackupRun, BackupRunAttempt, BackupArtifact ve BackupReplica kayıtları
- StorageConnection yapılandırmaları ve kota politikaları
- Denetim (Audit) günlüğü (`audit_events`) ve operasyonel kuyruklar

**Önemli:** Müşterilerin asıl yedek `.bak` artifact dosyaları storage hedeflerinde (Local disk, Central storage, SFTP, Google Drive) bağımsız olarak barınır. Metadata veritabanı bu dosyaların indeksini, checksum'larını ve sahipliğini yönetir.

---

## 2. Düzenli Yedekleme Prosedürü (Backup Runbook)

### 2.1 Güvenli Mantıksal Snapshot Alma
Platform PostgreSQL instance'ı üzerinde `pg_dump` binary formatı (`-F c`) ile sıkıştırılmış ve bütünlüğü korunmuş snapshot alınır:

```bash
# 1. Çevre değişkeni veya güvenli secret dosyasından bağlantı bilgisi temin edilir (asla komut geçmişine yazılmaz)
export PGPASSWORD="<postgres_admin_password>"

# 2. pg_dump ile binary formatta tam yedekleme
pg_dump -h 127.0.0.1 -p 5432 -U postgres -d backup_platform_dev \
    -F c -b -v \
    -f "/var/backups/platform-metadata/backup_platform_$(date +%Y%m%d_%H%M%S).dump"

# 3. Checksum oluşturma ve yetki sınırlandırma
chmod 600 /var/backups/platform-metadata/*.dump
sha256sum /var/backups/platform-metadata/*.dump > /var/backups/platform-metadata/checksums.sha256
```

---

## 3. İzole Afet Kurtarma Prosedürü (Disaster Recovery Runbook)

### 3.1 Adım Adım Kurtarma
1. **Bağlantıları Kesme:** Arızalı backend host'u durdurulur (`scripts/dev/stop.sh` veya systemd service stop).
2. **Yeni Hedef Oluşturma:** Temiz bir PostgreSQL 18 instance'ında hedef veritabanı oluşturulur:
   ```sql
   CREATE DATABASE backup_platform_recovered WITH OWNER = postgres ENCODING = 'UTF8';
   ```
3. **Bütünlük Doğrulaması:** Yedek dosyasının SHA256 checksum'ı doğrulanır:
   ```bash
   sha256sum -c checksums.sha256
   ```
4. **Veritabanı Geri Yükleme (`pg_restore`):**
   ```bash
   pg_restore -h 127.0.0.1 -p 5432 -U postgres -d backup_platform_recovered -v /path/to/dumpfile.dump
   ```
5. **Şema ve Veri Tutarlılık Doğrulaması:**
   ```sql
   SELECT count(*) FROM tenants;
   SELECT count(*) FROM agents;
   SELECT count(*) FROM backup_jobs;
   SELECT count(*) FROM backup_artifacts;
   SELECT count(*) FROM storage_connections;
   ```
6. **Backend Servis Yeniden Yönlendirme:** `ConnectionStrings:Platform` yeni kurtarılan veritabanına bağlanacak şekilde güncellenir ve servis ayağa kaldırılır.

---

## 4. Gerçekleştirilen İzole Tatbikat Sonuçları (2026-10-15)

Gerçekleştirilen DR tatbikatında canlı yerel geliştirme veritabanı (`backup_platform_dev`) anlık olarak yedeklenmiş, bağımsız `backup_platform_dev_dr_drill` veritabanına restore edilmiş ve satır tutarlılığı doğrulanmıştır:

| Tablo | Kaynak Kayıt Sayısı | Kurtarılan Kayıt Sayısı | Durum |
|---|---|---|---|
| `tenants` | 1 | 1 | **MATCH** |
| `agents` | 2 | 2 | **MATCH** |
| `backup_jobs` | 44 | 44 | **MATCH** |
| `backup_runs` | 44 | 44 | **MATCH** |
| `storage_connections` | 6 | 6 | **MATCH** |
| `__EFMigrationsHistory` | 27 | 27 | **MATCH** |

- **Yedek dosya boyutu:** 388.602 bayt (~380 KB compressed binary dump)
- **Dump süresi:** ~1,8 saniye
- **Restore süresi:** ~2,1 saniye
- **Toplam DR tatbikat süresi:** 4 saniye
- **Sonuç:** İzole test ortamında veritabanı başarıyla doğrulandıktan sonra tatbikat veritabanı (`backup_platform_dev_dr_drill`) temizlenmiştir (`DROP DATABASE`).
