# 06: T1.5 Güvenli Cutover, Temizlik ve Kabul Kapanışı

**What to build:** Eski harici storage relay/stream kodlarının ve geçici staging yollarının güvenle kaldırılması; geriye dönük mevcut verilerin doğrudan erişimle çalışmaya devam ettiğinin kanıtlanması ve T1 fazının canlı kabulle kapatılması.

**Blocked by:** 03: T1.2 Doğrudan Backup, 04: T1.3 Doğrudan Restore, 05: T1.4 Doğrudan Panel İndirmesi

**Status:** code-complete (live acceptance NOT RUN, see review-followups ticket 13)

- [x] Backend'deki harici storage relay endpoint'lerinin ve geçici transfer tamponlarının kaldırılması (Central/Platform Managed hariç)
- [x] Önceden alınmış mevcut replikaların ve artifact'lerin doğrudan restore ve indirme ile geriye uyumlu çalıştığının doğrulanması
- [ ] Gerçek ortam kabul matrisi: Canlı Google Drive, canlı SFTP, Windows VM ve Linux Agent üzerinde uçtan uca akış kanıtları
- [ ] Çalıştırılamayan test ve ortamların (örn. simetrik NAT sınırlamaları) açıkça `NOT RUN` olarak dokümante edilmesi
- [ ] `docs/plan/Proje_Durumu.md` ve yol haritasında T1 fazının `Completed` olarak güncellenmesi
