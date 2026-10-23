# 03: Doğrudan Depolama Ağ Kesintisi ve Otomatik Kurtarma (Faz 2C)

**What to build:** SFTP ve Google Drive doğrudan transferlerinde ağ kopmalarının otomatik olarak (3 kez) toparlanması, checkpoint offset'i üzerinden kaldığı yerden sürdürülmesi ve toparlanamayan kesintilerin güvenli `PausedByNetworkError` durumuna alınması.

**Blocked by:** 02: Dayanıklı Transfer Duraklatma ve Devam Ettirme (Pause / Resume & Checkpoint - Faz 2C)

**Status:** done

- [x] Google Drive doğrudan transferinde resumable session URI saklama ve range query ile son onaylanan bayttan stream'e devam etme.
- [x] SFTP doğrudan transferinde seek/append offset desteğiyle dosyanın sonundan yazmayı sürdürme.
- [x] Agent tarafında transient network hatası oluştuğunda exponential backoff ile 3 otomatik deneme mekanizması.
- [x] 3 deneme başarısız olduğunda attempt'i fail etmek yerine `PausedByNetworkError` durumuna alarak operatör müdahalesine olanak tanıma.
- [x] Mock network drop senaryolarıyla uçtan uca dayanıklılık entegrasyon testleri.
