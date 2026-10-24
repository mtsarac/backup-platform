# 05: T1.4 Doğrudan Panel İndirmesi ve Support Break-Glass

**What to build:** Web panelinden kullanıcı veya platform admin'in yedeği (şifreli ham dosya veya çözülmüş dosya) backend relay/stream kullanmadan, doğrudan tarayıcı ile yetkili Agent arasında kurulan güvenli doğrudan kanal üzerinden indirmesi.

**Blocked by:** 03: T1.2 Doğrudan Backup (Google Drive + SFTP Upload ve ReadBack), 04: T1.3 Doğrudan Restore, Agent Repair ve Yaşam Döngüsü

**Status:** code-complete (live acceptance NOT RUN, see review-followups ticket 13)

- [x] Tarayıcı ile Agent arasında tek kullanımlık, kısa ömürlü ve backend onaylı doğrudan aktarım oturumu başlatılması
- [x] Ham (şifreli) indirmelerde dosyanın bütünlük kontrolüyle doğrudan tarayıcıya/diske akıtılması
- [x] Çözülmüş (decrypted) indirme ve Support Break-Glass akışlarında, E1 dual-control ve anahtar çözme yetkisiyle Agent üzerinde plaintext hash doğrulanarak tarayıcıya iletilmesi
- [x] Doğrudan yol kurulamadığında (`DirectPathUnavailable`) açık ve anlaşılır hata sunulması; sessiz backend fallback veya TURN relay açılmaması
- [x] Aktarım boyunca backend ağ trafiğinin 0 dosya baytı olduğunun doğrulanması
