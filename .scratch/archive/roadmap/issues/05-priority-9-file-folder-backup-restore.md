# 05: Öncelik 9 — Faz 3 ve 9: Dosya ve Klasör Yedekleme ve Geri Yükleme

**What to build:** Veritabanı dışındaki genel dosya ve klasörlerin seçimi, include/exclude filtreleri, sembolik link yönetimi, dosya manifest'i üretimi ve güvenli geri yükleme (alternate path / overwrite) kabiliyeti (Bilinçli olarak yol haritasında en sona bırakılmıştır).

**Blocked by:** 03: Öncelik 7 (Temel depolama ve politika katmanları tamamlandıktan sonra)

**Status:** done

- [x] Faz 3: Dosya ve klasör seçim arayüzü, include/exclude wildcard kuralları
- [x] Faz 3: Açık/kilitli dosya davranışı ve dosya sistemi tarama (enumeration) / manifest oluşturma
- [x] Faz 9: Tüm klasörü veya seçilen dosyaları orijinal ya da alternatif dizine geri yükleme
- [x] İzinler, symlink ve dosya bütünlüğü doğrulaması

Linux gerçek dosya smoke kontrolünde tam/seçili restore, SHA-256, filtreler, symlink, Unix izinleri ve overwrite reddi doğrulandı. Gerçek UI tarayıcıda kontrol edildi; precheck sonrası hedef değişince restore yeniden kilitlenir. Windows DACL çalışma zamanı ayrı Windows host gerektirir ve bu ortamda doğrulanmadı.

Son güvenlik incelemesinde bulunan `a -> "."`, `x -> "a/.."` zincir kaçışı düzeltildi. Restore, seçilen manifest linklerinin ve mevcut hedef linklerinin ham bileşenlerini `.`/`..` sadeleştirmesinden önce denetler; zincirleri çözmek yerine reddeder ve tüm seçili linkleri dosya değiştirmeden önce doğrular. Normal kök içinde kalan göreli linkler desteklenir. 14 odaklı file-provider testi ve gerçek dosya/OS escape smoke kontrolü geçti; kötü arşiv mevcut hedef dosyayı değiştiremedi.
