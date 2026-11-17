# Tasarım dili prototipleri (ATILABİLİR)

Issue #176 için üç yön, tek dosyalık HTML. `file://` ile açılır, bağımlılık yok. Veriler örnektir.

- `variant-a.html` Sessiz operasyon konsolu (kenar çubuğu, yoğun liste)
- `variant-b.html` Durum ve kaynak→hedef (üst gezinme, geniş kartlar)
- `variant-c.html` Rehberli görev (tek sütun, ekran başına bir soru)

Her dosyada: `#home`, `#job-new`, açık/koyu anahtarı, "Yeni kullanıcı (boş)" durumu. Main'e birleştirilmez.

## A'nın çeşitlemeleri (aynı renk/yarıçap/gölge dili, farklı yapı)
- `variant-a1.html` Rahat A: 16 px, tek cümlelik durum + tek birincil eylem, 4 adımlı boş durum listesi, seçenek kartlı sihirbaz
- `variant-a2.html` İş satırı akışı: kaynak→hedef→son sonuç satırları, ince durum çubuğu, tek sayfa sihirbaz + yapışkan Özet
- `variant-a3.html` Dar ray + liste: ikon rayı, ortalanmış sütun, gruplu liste, arama/"Yeni", sihirbaz sağ çekmece
- `variant-a4.html` Çalışma alanı: sol iş listesi + sağ ayrıntı, üst adım çubuğu + canlı Özet

## Ana sayfa alternatifleri (A dili; A4'ün ana düzeni Yedeklemeler>İşler'e ayrıldı)
- `home-1.html` Yapılacaklar: eylem kuyruğu + ince şerit + son etkinlik
- `home-2.html` Koruma haritası: bilgisayara göre gruplu kaynaklar, 14 günlük tazelik şeridi, geri yüklenebilirlik
- `home-3.html` Etkinlik akışı: Bugün/Dün/Bu hafta zaman çizelgesi + dar sağ sütun
- `home-4.html` Üç soru: güvende mi / geri yükle (arama) / yer yetiyor mu

## Seçimlerin birleşimi
- `chosen.html`: A dili + Ana sayfa home-2 + Yedeklemeler>İşler A4 ana-detay + A4 sihirbaz. Rotalar `#home`, `#jobs`, `#job-new`; "Seçimlerim" paneli.

## Yedeklemeler > Geçmiş (#182)
- `history.html`: Çalıştırmalar + Yedek dosyaları tek "Geçmiş" sekmesi; ayrıntı yerleşimi çubuktan "Yan panel (A4)" / "Ayrı sayfa" seçilir; rol anahtarı (Kullanıcı Yöneticisi / Operatör); Geri yüklemeler, Paylaşımlar ve Depolama > Silinemeyen yedek dosyaları örnekleri. Tarih filtresi etkisiz.
