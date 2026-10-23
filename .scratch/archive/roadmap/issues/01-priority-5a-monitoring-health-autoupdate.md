# 01: Öncelik 5A — Kapasite İzleme, Sağlık, Anomali, Auto-Update ve İleri Audit

**What to build:** T1 doğrudan transfer fazı tamamlandıktan sonra; harici sağlayıcı kapasitesinin doğrudan Agent üzerinden sorgulanması (Faz 21), yedekleme sağlık puanlaması (Faz 22), boyut anomali tespiti (Faz 23), imzalı paketlerle Agent otomatik güncelleme (Faz 25), tamper-resistant ileri audit (Faz 28) ve gelişmiş raporlama (Faz 29).

**Blocked by:** T1: Agent Doğrudan Storage Transferi (06: T1.5)

**Status:** done

- [x] Faz 21: Storage provider kapasite sorgulaması (Drive quota / SFTP disk space) Agent üzerinden sorgulanıp panelde gösterilmesi
- [x] Faz 22: Backup health ve güvenilirlik metriklerinin hesaplanması
- [x] Faz 23: Beklenmedik boyut değişimlerinde istatistiksel anomali uyarısı üretilmesi
- [x] Faz 25: İmzalı release paketleri ve güvenli Agent auto-update mekanizması (Öncelik 5A.2)
- [x] Faz 28 & 29: Kriptografik mühürlü audit kayıtları ve başarı/başarısızlık raporları (Öncelik 5A.2)

Gerçek imzalı güncelleme smoke kontrolü 54.164.363 baytlık yayın paketini indirip 297 dosyayı doğrulayarak sahneledi; imza/metadata/paket bozulmaları aktif sürüm ve identity/config dosyalarını koruyarak reddedildi. Linux cutover ve rollback aynı dizindeki symlink için atomik `rename(2)` kullanır. 102 activation ve 101 rollback sırasında 2.089 eşzamanlı executable açılışında eksik `current` gözlenmedi. Audit imzaları değişen payload'ı yakalar; tüm kaydın silinmesine karşı bağımsız audit dışa aktarımı gerekir. Windows servis güncellemesi bu ortamda çalıştırılmadı.
