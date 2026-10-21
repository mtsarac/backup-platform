# 01: T1.0 Tasarım, Doğrudan Kanal Fizibilitesi ve ADR Kapısı

**What to build:** Tarayıcıya doğrudan panel indirmesi ve Agent-provider veri yollarının netleştirilmesi; WebRTC DataChannel veya alternatif doğrudan kanalın (NAT, Windows/Linux service, >4 GiB dosya, backpressure) laboratuvardan sonra ürün mimarisine taşınabilirliğinin kanıtlanması ve T1.0 ADR'sinin kilitlenmesi.

**Blocked by:** None (can start immediately)

**Status:** done

- [x] Tarayıcı doğrudan kanalında simetrik NAT/desteklenmeyen ortamlar için açık `DirectPathUnavailable` hata sözleşmesinin ve destek matrisinin netleşmesi
- [x] Windows LocalService ve Linux service altında public inbound port açmadan STUN/signaling üzerinden doğrudan veri aktarım sınırlarının belirlenmesi
- [x] >4 GiB ve streaming dosya indirmelerinde OPFS/tarayıcı disk akışı ile backpressure mekanizmasının mimari kurala bağlanması
- [x] T1.0 ADR belgesinin (`docs/plan/T1_0_Dogrudan_Transfer_ADR.md`) tüm açık kapılar (credential tehdit modeli, Google Drive token yetki sınırı, SFTP host key) ile onaylanması
