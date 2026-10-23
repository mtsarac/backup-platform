# 02: Dayanıklı Transfer Duraklatma ve Devam Ettirme (Pause / Resume & Checkpoint - Faz 2C)

**What to build:** Aktif bir yedekleme transferinin operatör veya zaman kuralı tarafından bağlantı ve veritabanı kilitleri serbest bırakılarak duraklatılması (`BackupStatus.Paused`), doğrulanmış byte offset'inin `TransferCheckpoint` olarak kaydedilmesi ve sonradan aynı noktadan devam ettirilmesi.

**Blocked by:** 01: Zaman Pencereli Bant Genişliği Profilleri ve Dinamik Hız Belirleme (Faz 2B)

**Status:** done

- [x] `BackupStatus.Paused` ve `TransferPauseReason` enum ve yaşam döngüsü kuralları.
- [x] `TransferCheckpoint` tablosu/modeli (attemptId, verifiedByteOffset, providerState, recordedAt).
- [x] REST API: `POST /api/backups/runs/{runId}/pause` ve `POST /api/backups/runs/{runId}/resume`.
- [x] Agent tarafında pause sinyalini alıp in-flight tamponu güvenle flush etme ve lease'i temiz bırakma.
- [x] Resume işleminde yeni bir lease açılarak Agent'a checkpoint offset'inin iletilmesi ve transferin kaldığı yerden sürmesi.
- [x] UI üzerinde "Duraklat" ve "Devam Et" butonları, durum rozetleri.
- [x] Entegrasyon testleri (duraklatma sırasında DB kilidi tutulmadığının ve resume sonrasında artifact bütünlüğünün doğrulanması).

Birleşik gerçek runtime regresyonu PostgreSQL dump'ı, kimlik doğrulamalı HTTP policy, `BackupTaskWorker`, governor, pipeline ve agent-direct local storage kullanır. Pozitif checkpoint'ten sonra deterministik saat sıfır-hız penceresine girer; worker manuel completion olmadan `Paused / TimeRule` kaydeder. Attempt lease ve bandwidth grant silinir, staging/partial upload korunur ve kaydedilmiş prefix SHA-256 eşitliği geçer. Resume yeni lease alır; duraklatma penceresi aktifken upload yaparak kuralı atlayamaz. Otomatik resume eklenmedi.
