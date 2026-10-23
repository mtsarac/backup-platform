# 02: Öncelik 6 — Faz 2B ve 2C: Saat Bazlı Transfer Profilleri ve Network Recovery

**What to build:** Tenant ve job bazlı zaman dilimine bağlı bant genişliği profilleri (Faz 2B: mesai saati/gece tarifesi, DST yönetimi) ile transfer duraklatma/devam ettirme ve ağ kesintisi toparlanması (Faz 2C: pause/resume, provider multipart checkpoint).

**Blocked by:** T1: Agent Doğrudan Storage Transferi (06: T1.5)

**Status:** done

- [x] Faz 2B: Tenant ve job seviyesinde saatlik bant genişliği profili (örn. mesai saatlerinde kısıtlı, geceleri sınırsız)
- [x] Faz 2B: Schedule time zone ve geceye taşan transfer pencerelerinde DST duyarlı limit geçişleri
- [x] Faz 2C: Kullanıcı tarafından transferi duraklatma (pause) ve güvenle devam ettirme (resume)
- [x] Faz 2C: Uzun transferlerde sağlayıcının (Drive / SFTP / S3) multipart checkpoint özellikleriyle ağ kesintisinden sonra baştan başlamadan toparlanma

Zaman kuralı için sıfır-hızlı profil penceresi kullanılabilir. Pencereye giriş checkpoint'li `TimeRule` pause üretir; pencereden çıkış otomatik resume yapmaz. Profil > statik varsayılan önceliği, aynı profilin farklı scope'larda kullanımı ve gerçek worker/HTTP/dump zincirinde otomatik pause odaklı regresyonlarla doğrulandı.
