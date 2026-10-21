# 04: Kompozit Backup Health Durumu ve Puanlama Motoru

**What to build:** Her yedekleme işi (job) ve tenant geneli için 0–100 arası kompozit bir güvenilirlik puanı ve tri-state sağlık durumu (`Healthy`, `Degraded`, `Critical`) hesaplayan değerlendirici motor. Puan 4 temel faktöre dayanır: Son yedek yaşı (%40), doğrulanmış replika sayısı (%30), izole restore testi (R1) sonucu (%20) ve depolama hedefi doluluğu (%10). Sağlık puanı ve durumu REST API ve kullanıcı arayüzünde gösterilir; düşük puan yedekleme çalıştırmasını asla engellemez.

**Blocked by:** 01: Hibrit Depolama Kapasitesi Sorgulama (CapacityProbe & API)

**Status:** done

- [x] `BackupHealthEvaluator` servisi 4 ağırlıklı bileşeni deterministik olarak hesaplar:
  - Freshness (40 puan): Planlanan periyot içindeyse tam puan, periyot 2 katına çıktıkça lineer azalarak 0 puan.
  - Replication (30 puan): Gerekli doğrulanmış replika sayısı sağlandıysa tam puan; eksikse oransal puan; verified kopya yoksa 0 puan.
  - Restore Verification (20 puan): Son 30 gün içinde R1 izole restore testi başarılıysa tam puan; test edilmemişse 10 puan; başarısızsa 0 puan.
  - Storage Capacity (10 puan): Hedef <%80 ise tam 10 puan; %80-%90 arası 5 puan; >=%90 ise 0 puan.
- [x] Sağlık sınıflandırması: 80–100 `Healthy` (Yeşil), 50–79 `Degraded` (Sarı), 0–49 `Critical` (Kırmızı).
- [x] REST API: `GET /api/v1/backups/jobs/{id}/health` endpoint'i puanı, durumunu ve 4 faktörün puan dökümünü döner.
- [x] REST API: `GET /api/v1/observability/health-summary` endpoint'i tenant genelindeki işlerin sağlık durum özetini döner.
- [x] Düşük sağlık puanı veya `Critical` durumu sonraki manuel veya zamanlanmış yedeklerin başlamasını asla engellemez (Advisory model).
- [x] Web panelinde Yedekleme İşi detayında sağlık puanı kartı, durum rozeti ve puanı düşüren etkenlerin açık açıklaması gösterilir.
- [x] Integration testleri: Mükemmel yedek (100 puan), gecikmiş/stale yedek (azalan puan), eksik replika ve başarısız restore durumları test edildi ve geçti.

## Verification

- Puanlama motoru ve API projeksiyonu doğrulandı; %90 kritik sınırındaki puanlama tek domain kuralına bağlandı.
- Gerçek HTTPS API üzerinde 4 faktörün hesaplandığı ve çalışan Chromium üzerinde `80 / 100` puan kartı ile faktör dökümlerinin gösterildiği doğrulandı.
