Status: done

## Amaç ve Temel Kural
Dosya platformun kendi storage'ında barınmıyorsa dosya baytları platform backend'inden, reverse proxy'sinden veya platformun işlettiği başka bir dosya relay'inden geçmez. Bu kural upload, readback, restore, precheck, harici hedefler arası repair, panel indirmesi ve break-glass indirmesini kapsar.

Backend yalnız kontrol düzlemidir (auth, lease, kota/rezervasyon, hız bütçesi, metadata, audit). Harici storage için **backend artifact payload baytı 0** olacaktır.

## Kapsam
- Sağlayıcılar: SFTP ve Google Drive (mevcut bağlantılar ve kimlik doğrulama modelleri).
- Agent desteği: Windows Service ve Linux Service.
- Veritabanı motorları: MSSQL ve PostgreSQL Full yedekleri.
- Format: Düz v1 ve E1 bpe v2 (istemci tarafı şifreli).

## Dikey Dilimler (Tracer Bullets)
1. **01 — T1.0 Tasarım, Doğrudan Kanal Fizibilitesi ve ADR Kapısı**
2. **02 — T1.1 Ortak Agent Execution, Yetkilendirme ve Relay Reddi**
3. **03 — T1.2 Doğrudan Backup (Google Drive + SFTP Upload/Readback)**
4. **04 — T1.3 Doğrudan Restore, Agent Repair ve Yaşam Döngüsü**
5. **05 — T1.4 Doğrudan Panel İndirmesi ve Support Break-Glass**
6. **06 — T1.5 Güvenli Cutover, Temizlik ve Kabul Kapanışı**
