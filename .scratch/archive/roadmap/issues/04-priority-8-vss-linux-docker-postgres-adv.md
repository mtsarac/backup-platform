# 04: Öncelik 8 — Faz 4-7, 13, 26, 27, 30: VSS, Linux Installer, Docker, PostgreSQL İleri, mTLS

**What to build:** Platform altyapısının işletim sistemi ve konteyner seviyesinde olgunlaştırılması; Windows VSS snapshot entegrasyonu (Faz 4), Linux paketleme/systemd kurulum otomasyonu (Faz 5), Linux snapshot sağlayıcıları (Faz 6), Docker runtime konteyner keşfi (Faz 7), PostgreSQL physical/streaming backup iterasyonu (Faz 13), mTLS sertifika tabanlı Agent kimlik doğrulaması (Faz 26) ve harici secret yöneticileri (Faz 27).

**Blocked by:** 03: Öncelik 7 (veya operasyonel ihtiyaç halinde öne alınabilir)

**Status:** done

- [x] Faz 4: Windows VSS (Volume Shadow Copy Service) snapshot yaşam döngüsü
- [x] Faz 5: Linux Agent için systemd service ve deb/rpm paketleme / installer otomasyonu
- [x] Faz 7: Docker socket üzerinden containerize veritabanı keşfi ve network tabanlı yedekleme
- [x] Faz 13: PostgreSQL ileri iterasyonu (physical/streaming backup gereksinimleri)
- [x] Faz 26: Agent mTLS sertifika kaydı ve rotasyonu
- [x] Faz 27: HashiCorp Vault / AWS Secrets Manager harici sağlayıcı entegrasyonu

Kod tamamlandı; gerçek PostgreSQL 18 physical/streaming kopyaları, PostgreSQL 16 istemci reddi ve Linux `.deb` paketi doğrulandı. Windows host çapraz derlendi ancak VM'e ağ erişimi olmadığından VSS, Windows ACL/SMB ve servis çalışma zamanı doğrulanamadı. Gerçek LVM/Btrfs/ZFS diskleri, RPM üretimi, systemd kurulum yaşam döngüsü ve canlı Vault/AWS hesapları bu ortamda doğrulanmadı; bunlar test geçmiş gibi değerlendirilmemelidir.
