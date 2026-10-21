# 02: T1.1 Ortak Agent Execution, Yetkilendirme ve Relay Reddi

**What to build:** Agent'ın harici depolama görevlerini (SFTP, Google Drive) doğrudan kendi üzerinde yürütebilmesi için görev modeli, kısa ömürlü credential alımı ve backend'in harici storage için yetkisiz dosya aktarımlarını reddetmesi.

**Blocked by:** 01: T1.0 Tasarım, Doğrudan Kanal Fizibilitesi ve ADR Kapısı

**Status:** done

- [x] `BuiltInStorageProviders` ve `BackupTaskRequestFactory` içinde AgentDirect execution lokasyonunun harici sağlayıcılar için aktif edilmesi
- [x] Backend'den Agent'a hassas credential/token'ların güvenli, cache edilmeyen, lease-bound kanalla iletilmesi
- [ ] Google Drive için Agent'a refresh token vermeden backend koordinasyonlu kısa ömürlü access token dağıtımının kurulması
- [ ] SFTP için host-key doğrulaması, tenant path izolasyonu ve Agent üzerinden bağlantı testi (`TestConnection`) capability'sinin sağlanması
- [ ] Eski veya güncellenmemiş Agent'ların harici relay taleplerinin `403/409` fail-closed reddedilmesi
